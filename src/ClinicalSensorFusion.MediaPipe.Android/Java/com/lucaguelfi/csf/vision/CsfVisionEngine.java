package com.lucaguelfi.csf.vision;

import android.content.Context;
import android.graphics.Bitmap;
import android.os.SystemClock;

import com.google.mediapipe.framework.image.BitmapImageBuilder;
import com.google.mediapipe.framework.image.MPImage;
import com.google.mediapipe.tasks.components.containers.Category;
import com.google.mediapipe.tasks.components.containers.Landmark;
import com.google.mediapipe.tasks.components.containers.NormalizedLandmark;
import com.google.mediapipe.tasks.core.BaseOptions;
import com.google.mediapipe.tasks.core.Delegate;
import com.google.mediapipe.tasks.vision.core.ImageProcessingOptions;
import com.google.mediapipe.tasks.vision.core.RunningMode;
import com.google.mediapipe.tasks.vision.facelandmarker.FaceLandmarker;
import com.google.mediapipe.tasks.vision.facelandmarker.FaceLandmarkerResult;
import com.google.mediapipe.tasks.vision.handlandmarker.HandLandmarker;
import com.google.mediapipe.tasks.vision.handlandmarker.HandLandmarkerResult;
import com.google.mediapipe.tasks.vision.poselandmarker.PoseLandmarker;
import com.google.mediapipe.tasks.vision.poselandmarker.PoseLandmarkerResult;

import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Facciata sui task MediaPipe (Pose, Hands, Face Landmarker) in modalità VIDEO: equivalente Android di
 * CSFVisionEngine del bridge iOS.
 *
 * Perché una facciata Java invece di legare direttamente MediaPipe a .NET:
 * - l'API MediaPipe è ampia (builder AutoValue, Optional, liste annidate) e cambia tra versioni; il
 *   binding .NET resta piccolo e stabile;
 * - i risultati passano il confine Java/.NET come pochi array di float invece che come migliaia di oggetti.
 *
 * Thread: con il delegato GPU un landmarker va creato e usato sullo stesso thread (contesto OpenGL).
 * Creazione, inferenza e chiusura girano quindi su un unico thread interno; le chiamate lo attendono.
 * Il chiamante deve comunque scartare i fotogrammi mentre un'inferenza è in corso (back-pressure).
 */
public final class CsfVisionEngine {
    private final ExecutorService worker = Executors.newSingleThreadExecutor(r -> new Thread(r, "csf-vision"));
    private final Context context;
    private final CsfVisionOptions options;
    private PoseLandmarker pose;
    private HandLandmarker hands;
    private FaceLandmarker face;
    private boolean gpu;
    private long lastTimestampMs = -1;

    private CsfVisionEngine(Context context, CsfVisionOptions options) {
        this.context = context.getApplicationContext();
        this.options = options;
    }

    /** Crea il motore; se la GPU non è utilizzabile su questo dispositivo ripiega sulla CPU. */
    public static CsfVisionEngine create(Context context, CsfVisionOptions options) throws Exception {
        if (options.getPoseModelPath() == null && options.getHandModelPath() == null && options.getFaceModelPath() == null) {
            throw new IllegalArgumentException("Nessun modello configurato.");
        }

        CsfVisionEngine engine = new CsfVisionEngine(context, options);
        try {
            engine.run(() -> {
                engine.open();
                return null;
            });
        } catch (Exception e) {
            engine.worker.shutdown();
            throw e;
        }

        return engine;
    }

    public boolean isGpu() { return gpu; }

    /**
     * Esegue i task configurati su un fotogramma.
     *
     * @param bitmap          immagine ARGB_8888 nell'orientamento del sensore
     * @param rotationDegrees rotazione oraria per vederla dritta (0, 90, 180, 270)
     * @param timestampMs     millisecondi monotoni crescenti (richiesto dalla modalità VIDEO)
     */
    public CsfVisionResult detect(Bitmap bitmap, int rotationDegrees, long timestampMs) throws Exception {
        return run(() -> {
            // MediaPipe rifiuta timestamp non strettamente crescenti.
            long ts = timestampMs > lastTimestampMs ? timestampMs : lastTimestampMs + 1;
            lastTimestampMs = ts;
            try {
                return infer(bitmap, rotationDegrees, ts);
            } catch (RuntimeException e) {
                if (!gpu) {
                    throw e;
                }

                // Alcune GPU accettano il modello ma falliscono al primo fotogramma: da qui in poi CPU.
                closeTasks();
                build(Delegate.CPU);
                gpu = false;
                return infer(bitmap, rotationDegrees, ts);
            }
        });
    }

    /** Rilascia i modelli. Dopo la chiusura il motore non è più utilizzabile. */
    public void close() {
        try {
            run(() -> {
                closeTasks();
                return null;
            });
        } catch (Exception ignored) {
            // La chiusura non deve far fallire chi sta smontando la pipeline.
        }

        worker.shutdown();
    }

    private <T> T run(Callable<T> work) throws Exception {
        try {
            return worker.submit(work).get();
        } catch (ExecutionException e) {
            Throwable cause = e.getCause();
            throw cause instanceof Exception ? (Exception) cause : e;
        }
    }

    private void open() {
        if (options.getUseGpu()) {
            try {
                build(Delegate.GPU);
                gpu = true;
                return;
            } catch (RuntimeException e) {
                closeTasks();
            }
        }

        build(Delegate.CPU);
        gpu = false;
    }

    private void build(Delegate delegate) {
        float confidence = options.getMinConfidence();

        if (options.getPoseModelPath() != null) {
            pose = PoseLandmarker.createFromOptions(context, PoseLandmarker.PoseLandmarkerOptions.builder()
                    .setBaseOptions(base(options.getPoseModelPath(), delegate))
                    .setRunningMode(RunningMode.VIDEO)
                    .setNumPoses(options.getMaxPoses())
                    .setMinPoseDetectionConfidence(confidence)
                    .setMinPosePresenceConfidence(confidence)
                    .setMinTrackingConfidence(confidence)
                    .build());
        }

        if (options.getHandModelPath() != null) {
            hands = HandLandmarker.createFromOptions(context, HandLandmarker.HandLandmarkerOptions.builder()
                    .setBaseOptions(base(options.getHandModelPath(), delegate))
                    .setRunningMode(RunningMode.VIDEO)
                    .setNumHands(options.getMaxHands())
                    .setMinHandDetectionConfidence(confidence)
                    .setMinHandPresenceConfidence(confidence)
                    .setMinTrackingConfidence(confidence)
                    .build());
        }

        if (options.getFaceModelPath() != null) {
            face = FaceLandmarker.createFromOptions(context, FaceLandmarker.FaceLandmarkerOptions.builder()
                    .setBaseOptions(base(options.getFaceModelPath(), delegate))
                    .setRunningMode(RunningMode.VIDEO)
                    .setNumFaces(options.getMaxFaces())
                    .setMinFaceDetectionConfidence(confidence)
                    .setMinFacePresenceConfidence(confidence)
                    .setMinTrackingConfidence(confidence)
                    .setOutputFaceBlendshapes(true)
                    .build());
        }
    }

    private static BaseOptions base(String modelAssetPath, Delegate delegate) {
        return BaseOptions.builder().setModelAssetPath(modelAssetPath).setDelegate(delegate).build();
    }

    private void closeTasks() {
        if (pose != null) {
            pose.close();
            pose = null;
        }

        if (hands != null) {
            hands.close();
            hands = null;
        }

        if (face != null) {
            face.close();
            face = null;
        }
    }

    private CsfVisionResult infer(Bitmap bitmap, int rotationDegrees, long ts) {
        long started = SystemClock.elapsedRealtimeNanos();
        // L'MPImage non va chiuso: chiuderlo ricicla la bitmap, che resta del chiamante (e serve ancora
        // se l'inferenza viene ripetuta sulla CPU).
        MPImage image = new BitmapImageBuilder(bitmap).build();
        ImageProcessingOptions processing = ImageProcessingOptions.builder().setRotationDegrees(rotationDegrees).build();

        int poseCount = 0;
        float[] poseLm = new float[0];
        float[] poseWorld = new float[0];
        if (pose != null) {
            PoseLandmarkerResult r = pose.detectForVideo(image, processing, ts);
            poseCount = r.landmarks().size();
            poseLm = packNormalized(r.landmarks());
            poseWorld = packWorld(r.worldLandmarks());
        }

        int handCount = 0;
        float[] handLm = new float[0];
        float[] handWorld = new float[0];
        String[] handedness = new String[0];
        if (hands != null) {
            HandLandmarkerResult r = hands.detectForVideo(image, processing, ts);
            handCount = r.landmarks().size();
            handLm = packNormalized(r.landmarks());
            handWorld = packWorld(r.worldLandmarks());
            List<List<Category>> sides = r.handedness();
            handedness = new String[sides.size()];
            for (int i = 0; i < handedness.length; i++) {
                handedness[i] = sides.get(i).isEmpty() ? "" : sides.get(i).get(0).categoryName();
            }
        }

        int faceCount = 0;
        float[] faceLm = new float[0];
        float[] blend = new float[0];
        String[] blendNames = new String[0];
        if (face != null) {
            FaceLandmarkerResult r = face.detectForVideo(image, processing, ts);
            faceCount = r.faceLandmarks().size();
            faceLm = packNormalized(r.faceLandmarks());
            if (r.faceBlendshapes().isPresent() && !r.faceBlendshapes().get().isEmpty()) {
                List<Category> categories = r.faceBlendshapes().get().get(0);
                blend = new float[categories.size()];
                blendNames = new String[categories.size()];
                for (int i = 0; i < blend.length; i++) {
                    blend[i] = categories.get(i).score();
                    blendNames[i] = categories.get(i).categoryName();
                }
            }
        }

        double inferenceMs = (SystemClock.elapsedRealtimeNanos() - started) / 1e6;
        return new CsfVisionResult(ts, inferenceMs, gpu,
                poseCount, poseLm, poseWorld,
                handCount, handLm, handWorld, handedness,
                faceCount, faceLm, blend, blendNames);
    }

    private static float[] packNormalized(List<List<NormalizedLandmark>> groups) {
        int n = 0;
        for (List<NormalizedLandmark> group : groups) {
            n += group.size();
        }

        float[] values = new float[n * CsfVisionResult.VALUES_PER_LANDMARK];
        int o = 0;
        for (List<NormalizedLandmark> group : groups) {
            for (NormalizedLandmark l : group) {
                values[o++] = l.x();
                values[o++] = l.y();
                values[o++] = l.z();
                values[o++] = l.visibility().orElse(1f);
                values[o++] = l.presence().orElse(1f);
            }
        }

        return values;
    }

    private static float[] packWorld(List<List<Landmark>> groups) {
        int n = 0;
        for (List<Landmark> group : groups) {
            n += group.size();
        }

        float[] values = new float[n * CsfVisionResult.VALUES_PER_LANDMARK];
        int o = 0;
        for (List<Landmark> group : groups) {
            for (Landmark l : group) {
                values[o++] = l.x();
                values[o++] = l.y();
                values[o++] = l.z();
                values[o++] = l.visibility().orElse(1f);
                values[o++] = l.presence().orElse(1f);
            }
        }

        return values;
    }
}
