using ClinicalSensorFusion.Core.Localization;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.MediaPipe;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using UIKit;

namespace ClinicalSensorFusion.App.Platforms.iOS.Vision;

/// <summary>
/// MediaPipe (tramite il bridge nativo) + fusione con il LiDAR, fotogramma per fotogramma.
/// </summary>
/// <remarks>
/// Back-pressure: si elabora un solo fotogramma alla volta e quelli che arrivano nel frattempo si
/// scartano (<see cref="TrySubmit"/> restituisce false). Con ARKit a 60 fps e un'inferenza da ~25 ms la
/// pipeline lavora a 20–30 fps senza accumulare latenza: per la clinica conta l'attualità del dato,
/// non elaborare ogni fotogramma in ritardo.
/// </remarks>
internal sealed class VisionPipeline(ILogger<VisionPipeline> logger) : IDisposable
{
    private const int Values = 5; // [x, y, z, visibility, presence] per punto, vedi CSFVisionResult.

    private static readonly (int From, int To)[] NoConnections = [];
    private static readonly string[] JointLabels = [Loc.T("Spalla sx"), Loc.T("Spalla dx"), Loc.T("Gomito sx"), Loc.T("Gomito dx"), Loc.T("Anca sx"), Loc.T("Anca dx"), Loc.T("Ginocchio sx"), Loc.T("Ginocchio dx"), Loc.T("Caviglia sx"), Loc.T("Caviglia dx")];

    private readonly PoseDepthFusion _fusion = new();

    // Protegge il motore nativo: un cambio di modalità non deve rilasciarlo mentre un'inferenza lo sta usando.
    private readonly Lock _engineLock = new();
    private CSFVisionEngine? _engine;
    private CaptureMode _mode;
    private int _busy;

    public event EventHandler<OverlayFrame>? OverlayUpdated;

    /// <summary>Destinazione dei dati durante la registrazione (null in sola anteprima).</summary>
    public (ISensorSink Sink, SensorClock Clock)? Recording { get; set; }

    public bool IsReady => _engine is not null;

    public string? LastError { get; private set; }

    /// <summary>Carica solo il modello MediaPipe necessario alla modalità.</summary>
    public void Configure(CaptureMode mode)
    {
        lock (_engineLock)
        {
            if (_engine is not null && _mode == mode)
            {
                return;
            }

            _engine?.Dispose();
            _engine = null;
            _mode = mode;
            _engine = CreateEngine(mode);
        }
    }

    private CSFVisionEngine? CreateEngine(CaptureMode mode)
    {

        using var options = new CSFVisionOptions
        {
            UseGpu = true,
            MinConfidence = 0.5f,
            MaxPoses = 1,
            MaxHands = 2,
            MaxInputDimension = 640,
        };

        switch (mode)
        {
            case CaptureMode.BodyLidar:
                options.PoseModelPath = ModelPath("pose_landmarker_full");
                break;
            case CaptureMode.HandsLidar:
                options.HandModelPath = ModelPath("hand_landmarker");
                break;
        }

        var engine = CSFVisionEngine.Create(options, out var error);
        LastError = error?.LocalizedDescription;
        if (engine is null)
        {
            logger.LogError("MediaPipe non inizializzato: {Error}", LastError);
        }

        return engine;
    }

    /// <summary>Accoda un fotogramma se la pipeline è libera; altrimenti lo rilascia subito.</summary>
    public bool TrySubmit(ArFrameJob job)
    {
        if (_engine is null || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            job.Dispose();
            return false;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Process(job);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Errore nell'elaborazione del fotogramma.");
            }
            finally
            {
                job.Dispose();
                Volatile.Write(ref _busy, 0);
            }
        });
        return true;
    }

    public void Dispose()
    {
        lock (_engineLock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }

    private void Process(ArFrameJob job)
    {
        lock (_engineLock)
        {
            if (_engine is { } engine)
            {
                Process(engine, _mode, job);
            }
        }
    }

    private void Process(CSFVisionEngine engine, CaptureMode mode, ArFrameJob job)
    {

        // Camera posteriore in verticale: immagine da ruotare di 90° orari. Frontale: 90° antiorari.
        // La rotazione serve solo all'inferenza (i modelli vogliono il soggetto dritto): MediaPipe su iOS
        // restituisce comunque i punti nel sistema dell'immagine originale, cioè quello del sensore, lo
        // stesso di profondità LiDAR, intrinseci e displayTransform. Nessuna conversione, quindi.
        const ImageOrientation orientation = ImageOrientation.Up;
        const UIImageOrientation uiOrientation = UIImageOrientation.Right;

        var inferenceStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var result = engine.Detect(job.Image, uiOrientation, (nint)(job.Timestamp * 1000), out var error);
        ClinicalSensorFusion.Core.Telemetry.CsfTelemetry.Capture.VisionInferenceDuration.Record(
            System.Diagnostics.Stopwatch.GetElapsedTime(inferenceStarted).TotalSeconds,
            ClinicalSensorFusion.Core.Telemetry.CsfTelemetry.Tag("mode", mode.ToString()));
        if (result is null)
        {
            logger.LogWarning("Inferenza MediaPipe fallita: {Error}", error?.LocalizedDescription);
            return;
        }

        var recording = Recording;
        var t = recording is { } r ? r.Clock.FromUptime(job.Timestamp) : 0;

        OverlayFrame? overlay = mode switch
        {
            CaptureMode.BodyLidar when result.PoseCount > 0 => ProcessPose(result, job, orientation, recording, t),
            CaptureMode.HandsLidar => ProcessHands(result, job, orientation, recording, t),
            _ => null,
        };

        OverlayUpdated?.Invoke(this, overlay ?? new OverlayFrame([], NoConnections, Loc.T("Nessun soggetto rilevato"), result.InferenceMs));
    }

    private OverlayFrame ProcessPose(CSFVisionResult result, ArFrameJob job, ImageOrientation orientation, (ISensorSink Sink, SensorClock Clock)? recording, double t)
    {
        var raw = ReadFloats(result.PoseLandmarks);
        var rawWorld = ReadFloats(result.PoseWorldLandmarks);
        var landmarks = new NormalizedLandmark[PoseLandmark.Count];
        var world = new Vector3[PoseLandmark.Count];
        for (var i = 0; i < PoseLandmark.Count; i++)
        {
            var o = i * Values;
            landmarks[i] = new NormalizedLandmark(raw[o], raw[o + 1], raw[o + 2], raw[o + 3]);
            world[i] = rawWorld.Length >= (i + 1) * Values ? new Vector3(rawWorld[o], rawWorld[o + 1], rawWorld[o + 2]) : new Vector3(float.NaN);
        }

        FusedPose fused = job.Depth is { } depth
            ? _fusion.Fuse(landmarks, world, orientation, depth, job.Intrinsics)
            : new FusedPose(world, 0, PoseSource.MediaPipeWorld, float.NaN);

        var angles = JointAngleCalculator.Compute(fused.Points);

        if (recording is { } rec)
        {
            var pose2d = new float[PoseLandmark.Count * 3];
            var pose3d = new float[PoseLandmark.Count * 3];
            for (var i = 0; i < PoseLandmark.Count; i++)
            {
                var (x, y) = OrientationMapper.ToSensor(landmarks[i].X, landmarks[i].Y, orientation);
                pose2d[i * 3] = x;
                pose2d[i * 3 + 1] = y;
                pose2d[i * 3 + 2] = landmarks[i].Visibility;
                pose3d[i * 3] = fused.Points[i].X;
                pose3d[i * 3 + 1] = fused.Points[i].Y;
                pose3d[i * 3 + 2] = fused.Points[i].Z;
            }

            var angleValues = new float[angles.Length + 2];
            angles.CopyTo(angleValues, 0);
            angleValues[^2] = fused.Source == PoseSource.Lidar ? 0 : 1;
            angleValues[^1] = fused.SubjectDistance;

            rec.Sink.TryWrite(new SensorReading(SensorKind.PoseLandmarks2D, t, pose2d));
            rec.Sink.TryWrite(new SensorReading(SensorKind.PoseLandmarks3D, t, pose3d));
            rec.Sink.TryWrite(new SensorReading(SensorKind.JointAngles, t, angleValues));
        }

        var points = new OverlayPoint[PoseLandmark.Count];
        for (var i = 0; i < points.Length; i++)
        {
            var lm = landmarks[i];
            var valid = lm.Visibility >= _fusion.MinVisibility;
            var kind = fused.Source == PoseSource.Lidar && float.IsFinite(fused.Points[i].Z) ? OverlayPointKind.Lidar : OverlayPointKind.Image;
            points[i] = ToView(lm.X, lm.Y, orientation, job.DisplayTransform, valid, kind);
        }

        return new OverlayFrame(points, PoseLandmark.Connections, PoseCaption(angles, fused), result.InferenceMs);
    }

    private static OverlayFrame? ProcessHands(CSFVisionResult result, ArFrameJob job, ImageOrientation orientation, (ISensorSink Sink, SensorClock Clock)? recording, double t)
    {
        var count = (int)result.HandCount;
        var raw = ReadFloats(result.HandLandmarks);
        var handedness = result.Handedness;

        if (recording is { } rec)
        {
            // Layout HandLandmarks: [n, (lato 0=sx/1=dx, profondità polso LiDAR (m), 21 × [x, y, z]) × n], x/y nel sistema del sensore.
            var values = new List<float>(1 + count * (2 + HandLandmark.Count * 3)) { count };
            for (var h = 0; h < count; h++)
            {
                values.Add(h < handedness.Length && handedness[h] == "Right" ? 1 : 0);
                var wrist = h * HandLandmark.Count * Values;
                var (wx, wy) = OrientationMapper.ToSensor(raw[wrist], raw[wrist + 1], orientation);
                values.Add(job.Depth?.SampleMedian(wx, wy) ?? float.NaN);
                for (var i = 0; i < HandLandmark.Count; i++)
                {
                    var o = (h * HandLandmark.Count + i) * Values;
                    var (x, y) = OrientationMapper.ToSensor(raw[o], raw[o + 1], orientation);
                    values.Add(x);
                    values.Add(y);
                    values.Add(raw[o + 2]);
                }
            }

            rec.Sink.TryWrite(new SensorReading(SensorKind.HandLandmarks, t, [.. values]));
        }

        if (count == 0)
        {
            return null;
        }

        var points = new OverlayPoint[count * HandLandmark.Count];
        var connections = new List<(int, int)>(count * HandLandmark.Connections.Length);
        for (var h = 0; h < count; h++)
        {
            for (var i = 0; i < HandLandmark.Count; i++)
            {
                var o = (h * HandLandmark.Count + i) * Values;
                points[h * HandLandmark.Count + i] = ToView(raw[o], raw[o + 1], orientation, job.DisplayTransform, true, OverlayPointKind.Hand);
            }

            foreach (var (a, b) in HandLandmark.Connections)
            {
                connections.Add((h * HandLandmark.Count + a, h * HandLandmark.Count + b));
            }
        }

        var caption = string.Join(" · ", Enumerable.Range(0, count).Select(h =>
            h < handedness.Length ? (handedness[h] == "Right" ? Loc.T("Mano dx") : Loc.T("Mano sx")) : Loc.T("Mano")));
        return new OverlayFrame(points, connections, caption, result.InferenceMs);
    }


    /// <summary>Coordinate orientate MediaPipe → sensore → vista (con la displayTransform di ARKit, che include rotazione, ritaglio aspect-fill ed eventuale specchiatura).</summary>
    private static OverlayPoint ToView(float u, float v, ImageOrientation orientation, CGAffineTransform display, bool valid, OverlayPointKind kind)
    {
        var (x, y) = OrientationMapper.ToSensor(u, v, orientation);
        var p = display.TransformPoint(new CGPoint(x, y));
        return new OverlayPoint((float)p.X, (float)p.Y, valid, kind);
    }

    private static string PoseCaption(float[] angles, FusedPose fused)
    {
        var parts = new List<string>(3);
        foreach (var joint in (ReadOnlySpan<Joint>)[Joint.RightKneeFlexion, Joint.RightElbowFlexion, Joint.RightShoulderElevation])
        {
            var a = angles[(int)joint];
            if (float.IsFinite(a))
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{JointLabels[(int)joint]} {a:F0}°"));
            }
        }

        if (float.IsFinite(fused.SubjectDistance))
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{fused.SubjectDistance:F2} m"));
        }

        parts.Add(fused.Source == PoseSource.Lidar ? "LiDAR" : Loc.T("stima 3D"));
        return string.Join(" · ", parts);
    }

    private static float[] ReadFloats(NSData data)
    {
        var result = new float[(int)data.Length / sizeof(float)];
        if (result.Length > 0)
        {
            Marshal.Copy(data.Bytes, result, 0, result.Length);
        }

        return result;
    }

    private static string? ModelPath(string name) =>
        NSBundle.MainBundle.PathForResource(name, "task", "models");
}
