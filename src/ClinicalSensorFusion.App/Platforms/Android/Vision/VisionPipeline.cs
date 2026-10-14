using ClinicalSensorFusion.Core.Localization;
using System.Globalization;
using System.Numerics;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.MediaPipe;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Platforms.Android.Vision;

/// <summary>
/// MediaPipe (tramite la facciata Java) fotogramma per fotogramma, con gli stessi flussi e layout dell'app iOS.
/// </summary>
/// <remarks>
/// Differenze rispetto a iOS, tutte dovute all'hardware:
/// <list type="bullet">
/// <item>senza LiDAR i punti 3D sono sempre i <i>world landmarks</i> di MediaPipe ("stima 3D");</item>
/// <item>come su iOS, MediaPipe usa la rotazione solo per l'inferenza e restituisce i punti nel sistema
/// del sensore (immagine orizzontale): così vanno in archivio, mentre per l'overlay si raddrizzano.</item>
/// </list>
/// </remarks>
internal sealed class VisionPipeline(ILogger<VisionPipeline> logger) : IDisposable
{
    private const int Values = CsfVisionResult.ValuesPerLandmark;
    private const float MinVisibility = 0.5f;

    private static readonly (int From, int To)[] NoConnections = [];
    private static readonly string[] JointLabels = [Loc.T("Spalla sx"), Loc.T("Spalla dx"), Loc.T("Gomito sx"), Loc.T("Gomito dx"), Loc.T("Anca sx"), Loc.T("Anca dx"), Loc.T("Ginocchio sx"), Loc.T("Ginocchio dx"), Loc.T("Caviglia sx"), Loc.T("Caviglia dx")];

    // Protegge il motore nativo: un cambio di modalità non deve rilasciarlo mentre un'inferenza lo sta usando.
    private readonly Lock _engineLock = new();
    private CsfVisionEngine? _engine;
    private CaptureMode _mode;

    public event EventHandler<OverlayFrame>? OverlayUpdated;

    /// <summary>Destinazione dei dati durante la registrazione (null in sola anteprima).</summary>
    public (ISensorSink Sink, SensorClock Clock)? Recording { get; set; }

    public bool IsReady => _engine is not null;

    /// <summary>Vero se l'inferenza gira sulla GPU; falso se il motore ha dovuto ripiegare sulla CPU.</summary>
    public bool IsGpu { get; private set; }

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

            Release();
            _mode = mode;
            _engine = CreateEngine(mode);
            IsGpu = _engine?.IsGpu == true;
        }
    }

    /// <summary>Esegue l'inferenza sul fotogramma (thread dell'analisi della camera).</summary>
    public void Process(CameraFrame frame)
    {
        lock (_engineLock)
        {
            if (_engine is { } engine)
            {
                Process(engine, _mode, frame);
            }
        }
    }

    public void Dispose()
    {
        lock (_engineLock)
        {
            Release();
        }
    }

    private void Release()
    {
        _engine?.Close();
        _engine?.Dispose();
        _engine = null;
    }

    private CsfVisionEngine? CreateEngine(CaptureMode mode)
    {
        using var options = new CsfVisionOptions
        {
            UseGpu = true,
            MinConfidence = 0.5f,
            MaxPoses = 1,
            MaxHands = 2,
        };

        // Percorsi negli asset dell'APK (Resources/Raw/models).
        switch (mode)
        {
            case CaptureMode.BodyLidar:
                options.PoseModelPath = "models/pose_landmarker_full.task";
                break;
            case CaptureMode.HandsLidar:
                options.HandModelPath = "models/hand_landmarker.task";
                break;
        }

        try
        {
            LastError = null;
            return CsfVisionEngine.Create(global::Android.App.Application.Context, options);
        }
        catch (Java.Lang.Exception ex)
        {
            LastError = ex.Message;
            logger.LogError(ex, "MediaPipe non inizializzato.");
            return null;
        }
    }

    private void Process(CsfVisionEngine engine, CaptureMode mode, CameraFrame frame)
    {
        var inferenceStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var result = engine.Detect(frame.Image, frame.RotationDegrees, (long)(frame.Timestamp * 1000));
        ClinicalSensorFusion.Core.Telemetry.CsfTelemetry.Capture.VisionInferenceDuration.Record(
            System.Diagnostics.Stopwatch.GetElapsedTime(inferenceStarted).TotalSeconds,
            ClinicalSensorFusion.Core.Telemetry.CsfTelemetry.Tag("mode", mode.ToString()));
        if (result is null)
        {
            return;
        }

        // Il ripiego sulla CPU può avvenire anche al primo fotogramma, non solo alla creazione.
        if (IsGpu != result.IsGpu)
        {
            IsGpu = result.IsGpu;
            logger.LogWarning("MediaPipe: inferenza su {Device}.", IsGpu ? "GPU" : "CPU (GPU non utilizzabile)");
        }

        var recording = Recording;
        var t = recording is { } r ? r.Clock.FromUptime(frame.Timestamp) : 0;
        var view = new ViewMapping(frame);

        OverlayFrame? overlay = mode switch
        {
            CaptureMode.BodyLidar when result.PoseCount > 0 => ProcessPose(result, view, recording, t),
            CaptureMode.HandsLidar => ProcessHands(result, view, recording, t),
            _ => null,
        };

        OverlayUpdated?.Invoke(this, overlay ?? new OverlayFrame([], NoConnections, Loc.T("Nessun soggetto rilevato"), result.InferenceMs));
    }

    private static OverlayFrame ProcessPose(CsfVisionResult result, ViewMapping view, (ISensorSink Sink, SensorClock Clock)? recording, double t)
    {
        var raw = result.GetPoseLandmarks() ?? [];
        var rawWorld = result.GetPoseWorldLandmarks() ?? [];
        if (raw.Length < PoseLandmark.Count * Values)
        {
            return new OverlayFrame([], NoConnections, Loc.T("Nessun soggetto rilevato"), result.InferenceMs);
        }

        // Come su iOS senza LiDAR: punti 3D di MediaPipe, NaN per le articolazioni poco visibili.
        var world = new Vector3[PoseLandmark.Count];
        for (var i = 0; i < PoseLandmark.Count; i++)
        {
            var o = i * Values;
            world[i] = rawWorld.Length >= o + Values ? new Vector3(rawWorld[o], rawWorld[o + 1], rawWorld[o + 2]) : new Vector3(float.NaN);
        }

        var fused = new FusedPose(world, 0, PoseSource.MediaPipeWorld, float.NaN);
        var angles = JointAngleCalculator.Compute(fused.Points);

        if (recording is { } rec)
        {
            var pose2d = new float[PoseLandmark.Count * 3];
            var pose3d = new float[PoseLandmark.Count * 3];
            for (var i = 0; i < PoseLandmark.Count; i++)
            {
                pose2d[i * 3] = raw[i * Values];
                pose2d[(i * 3) + 1] = raw[(i * Values) + 1];
                pose2d[(i * 3) + 2] = raw[(i * Values) + 3];
                pose3d[i * 3] = world[i].X;
                pose3d[(i * 3) + 1] = world[i].Y;
                pose3d[(i * 3) + 2] = world[i].Z;
            }

            var angleValues = new float[angles.Length + 2];
            angles.CopyTo(angleValues, 0);
            angleValues[^2] = 1; // sorgente 3D: MediaPipe world
            angleValues[^1] = float.NaN;

            rec.Sink.TryWrite(new SensorReading(SensorKind.PoseLandmarks2D, t, pose2d));
            rec.Sink.TryWrite(new SensorReading(SensorKind.PoseLandmarks3D, t, pose3d));
            rec.Sink.TryWrite(new SensorReading(SensorKind.JointAngles, t, angleValues));
        }

        var points = new OverlayPoint[PoseLandmark.Count];
        for (var i = 0; i < points.Length; i++)
        {
            var o = i * Values;
            points[i] = view.ToView(raw[o], raw[o + 1], raw[o + 3] >= MinVisibility, OverlayPointKind.Image);
        }

        return new OverlayFrame(points, PoseLandmark.Connections, PoseCaption(angles), result.InferenceMs);
    }

    private static OverlayFrame? ProcessHands(CsfVisionResult result, ViewMapping view, (ISensorSink Sink, SensorClock Clock)? recording, double t)
    {
        var raw = result.GetHandLandmarks() ?? [];
        var count = Math.Min(result.HandCount, raw.Length / (HandLandmark.Count * Values));
        var handedness = result.GetHandedness() ?? [];

        if (recording is { } rec)
        {
            // Layout HandLandmarks: [n, (lato 0=sx/1=dx, profondità del polso (m, qui assente), 21 × [x, y, z]) × n], x/y nel sistema del sensore.
            var values = new List<float>(1 + (count * (2 + (HandLandmark.Count * 3)))) { count };
            for (var h = 0; h < count; h++)
            {
                values.Add(h < handedness.Length && handedness[h] == "Right" ? 1 : 0);
                values.Add(float.NaN);
                for (var i = 0; i < HandLandmark.Count; i++)
                {
                    var o = ((h * HandLandmark.Count) + i) * Values;
                    values.Add(raw[o]);
                    values.Add(raw[o + 1]);
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
                var o = ((h * HandLandmark.Count) + i) * Values;
                points[(h * HandLandmark.Count) + i] = view.ToView(raw[o], raw[o + 1], true, OverlayPointKind.Hand);
            }

            foreach (var (a, b) in HandLandmark.Connections)
            {
                connections.Add(((h * HandLandmark.Count) + a, (h * HandLandmark.Count) + b));
            }
        }

        var caption = string.Join(" · ", Enumerable.Range(0, count).Select(h =>
            h < handedness.Length ? (handedness[h] == "Right" ? "Mano dx" : "Mano sx") : "Mano"));
        return new OverlayFrame(points, connections, caption, result.InferenceMs);
    }


    private static string PoseCaption(float[] angles)
    {
        var parts = new List<string>(4);
        foreach (var joint in (ReadOnlySpan<Joint>)[Joint.RightKneeFlexion, Joint.RightElbowFlexion, Joint.RightShoulderElevation])
        {
            var a = angles[(int)joint];
            if (float.IsFinite(a))
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{JointLabels[(int)joint]} {a:F0}°"));
            }
        }

        parts.Add(Loc.T("stima 3D"));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Passaggi di coordinate di un fotogramma. MediaPipe restituisce i punti nel sistema del sensore:
    /// per l'archivio restano così, per l'overlay si portano sull'immagine dritta e poi sulla vista di anteprima.
    /// </summary>
    private readonly struct ViewMapping
    {
        private readonly ImageOrientation _orientation;
        private readonly float _scaleX;
        private readonly float _scaleY;

        public ViewMapping(CameraFrame frame)
        {
            _orientation = frame.RotationDegrees switch
            {
                90 => ImageOrientation.Right,
                180 => ImageOrientation.Down,
                270 => ImageOrientation.Left,
                _ => ImageOrientation.Up,
            };

            // L'anteprima riempie la vista ritagliando l'eccedenza al centro: stessa scala sui due assi.
            var sideways = frame.RotationDegrees is 90 or 270;
            float width = sideways ? frame.Image.Height : frame.Image.Width;
            float height = sideways ? frame.Image.Width : frame.Image.Height;
            if (frame.ViewWidth > 0 && frame.ViewHeight > 0 && width > 0 && height > 0)
            {
                var scale = Math.Max(frame.ViewWidth / width, frame.ViewHeight / height);
                _scaleX = width * scale / frame.ViewWidth;
                _scaleY = height * scale / frame.ViewHeight;
            }
            else
            {
                _scaleX = _scaleY = 1;
            }
        }

        public OverlayPoint ToView(float x, float y, bool valid, OverlayPointKind kind)
        {
            var (u, v) = OrientationMapper.ToOriented(x, y, _orientation);

            return new OverlayPoint(0.5f + ((u - 0.5f) * _scaleX), 0.5f + ((v - 0.5f) * _scaleY), valid, kind);
        }
    }
}
