package com.lucaguelfi.csf.vision;

/**
 * Risultato di un fotogramma. I punti sono impacchettati in array di float per non creare migliaia di
 * oggetti Java (e relativi wrapper .NET) a ogni fotogramma: il volto da solo ha 478 punti.
 *
 * Layout di ogni punto: [x, y, z, visibility, presence] (VALUES_PER_LANDMARK = 5).
 * - *Landmarks: x, y normalizzati 0..1 sull'immagine ORIGINALE (quella del sensore, non ruotata: la
 *   rotazione serve solo all'inferenza); z relativo (scala circa x).
 * - *WorldLandmarks: metri, origine al centro delle anche (pose) o della mano.
 * - faceBlendshapes: coefficienti 0..1 del primo volto, con i nomi in faceBlendshapeNames.
 */
public final class CsfVisionResult {
    public static final int VALUES_PER_LANDMARK = 5;

    private final long timestampMs;
    private final double inferenceMs;
    private final boolean gpu;
    private final int poseCount;
    private final float[] poseLandmarks;
    private final float[] poseWorldLandmarks;
    private final int handCount;
    private final float[] handLandmarks;
    private final float[] handWorldLandmarks;
    private final String[] handedness;
    private final int faceCount;
    private final float[] faceLandmarks;
    private final float[] faceBlendshapes;
    private final String[] faceBlendshapeNames;

    CsfVisionResult(long timestampMs, double inferenceMs, boolean gpu,
                    int poseCount, float[] poseLandmarks, float[] poseWorldLandmarks,
                    int handCount, float[] handLandmarks, float[] handWorldLandmarks, String[] handedness,
                    int faceCount, float[] faceLandmarks, float[] faceBlendshapes, String[] faceBlendshapeNames) {
        this.timestampMs = timestampMs;
        this.inferenceMs = inferenceMs;
        this.gpu = gpu;
        this.poseCount = poseCount;
        this.poseLandmarks = poseLandmarks;
        this.poseWorldLandmarks = poseWorldLandmarks;
        this.handCount = handCount;
        this.handLandmarks = handLandmarks;
        this.handWorldLandmarks = handWorldLandmarks;
        this.handedness = handedness;
        this.faceCount = faceCount;
        this.faceLandmarks = faceLandmarks;
        this.faceBlendshapes = faceBlendshapes;
        this.faceBlendshapeNames = faceBlendshapeNames;
    }

    public long getTimestampMs() { return timestampMs; }
    public double getInferenceMs() { return inferenceMs; }
    /** Vero se l'inferenza gira sulla GPU, falso se il motore ha ripiegato sulla CPU. */
    public boolean isGpu() { return gpu; }

    public int getPoseCount() { return poseCount; }
    public float[] getPoseLandmarks() { return poseLandmarks; }
    public float[] getPoseWorldLandmarks() { return poseWorldLandmarks; }

    public int getHandCount() { return handCount; }
    public float[] getHandLandmarks() { return handLandmarks; }
    public float[] getHandWorldLandmarks() { return handWorldLandmarks; }
    /** "Left" / "Right" per ciascuna mano (lateralità come appare nell'immagine). */
    public String[] getHandedness() { return handedness; }

    public int getFaceCount() { return faceCount; }
    public float[] getFaceLandmarks() { return faceLandmarks; }
    public float[] getFaceBlendshapes() { return faceBlendshapes; }
    public String[] getFaceBlendshapeNames() { return faceBlendshapeNames; }
}
