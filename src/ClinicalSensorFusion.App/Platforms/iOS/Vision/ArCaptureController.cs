using ClinicalSensorFusion.Core.Localization;
using ARKit;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Sensors;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using UIKit;

namespace ClinicalSensorFusion.App.Platforms.iOS.Vision;

/// <summary>
/// Gestisce la sessione ARKit: fotocamera, LiDAR (sceneDepth), TrueDepth (face tracking) e stima della
/// luce ambientale. Smista ogni fotogramma a MediaPipe e ai flussi registrati.
/// </summary>
/// <remarks>
/// Si usa ARKit e non AVFoundation perché fornisce, sincronizzati sullo stesso fotogramma: immagine,
/// profondità LiDAR allineata, confidenza, intrinseci calibrati e posa della camera. Con AVFoundation
/// andrebbero allineati a mano (AVCaptureDataOutputSynchronizer + LiDAR depth format).
/// </remarks>
internal sealed class ArCaptureController : ARSessionDelegate
{
    private readonly ILogger _logger;
    private readonly VisionPipeline _vision;
    private readonly DispatchQueue _queue = new("csf.arkit", concurrent: false);
    private readonly Lock _viewportLock = new();
    private CGSize _viewport = new(390, 844);
    private double _lastLightReading = double.NegativeInfinity;
    private double _lastDepthReading = double.NegativeInfinity;

    public ArCaptureController(VisionPipeline vision, ILogger logger)
    {
        _vision = vision;
        _logger = logger;
        Session = new ARSession { Delegate = this, DelegateQueue = _queue };
    }

    public ARSession Session { get; }

    public CaptureMode Mode { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>Richiesta dell'operatore di usare la profondità LiDAR; vale dal prossimo <see cref="Run"/>.</summary>
    public bool UseLidar { get; set; }

    /// <summary>La sessione in corso sta producendo la mappa di profondità.</summary>
    public bool IsLidarActive { get; private set; }

    /// <summary>Sink della registrazione in corso (null in anteprima).</summary>
    public (ISensorSink Sink, SensorClock Clock)? Recording { get; set; }

    /// <summary>Opacità della mappa di profondità sull'anteprima (0 = disattivata).</summary>
    public float DepthOverlayOpacity { get; set; } = 0.35f;

    /// <summary>Dimensione della vista di anteprima: serve a calcolare la displayTransform per l'overlay.</summary>
    public CGSize Viewport
    {
        get
        {
            lock (_viewportLock)
            {
                return _viewport;
            }
        }

        set
        {
            if (value.Width <= 0 || value.Height <= 0)
            {
                return;
            }

            lock (_viewportLock)
            {
                _viewport = value;
            }
        }
    }

    public static bool LidarSupported => ARWorldTrackingConfiguration.SupportsFrameSemantics(ARFrameSemantics.SceneDepth);

    private PreviewFrame? _preview;

    public void Run(CaptureMode mode)
    {
        var configuration = new ARWorldTrackingConfiguration { LightEstimationEnabled = true };
        var lidar = false;
        // Il LiDAR si accende solo se l'operatore lo chiede, e solo in modalità corpo (angoli articolari
        // in metri reali). Altrimenti basta MediaPipe e si evita il costo della profondità a ogni fotogramma.
        if (mode == CaptureMode.BodyLidar && UseLidar && LidarSupported)
        {
            // sceneDepth "grezza" e non smoothedSceneDepth: lo smoothing temporale introduce ritardo
            // sui movimenti rapidi degli arti, falsando le escursioni massime.
            configuration.FrameSemantics = ARFrameSemantics.SceneDepth;
            lidar = true;
        }

        Mode = mode;
        IsLidarActive = lidar;
        _vision.Configure(mode);
        Session.Run(configuration, ARSessionRunOptions.ResetTracking | ARSessionRunOptions.RemoveExistingAnchors);
        IsRunning = true;
    }

    /// <summary>Ultimo fotogramma da disegnare; chi lo prende ne diventa proprietario. Null se non ce n'è uno nuovo.</summary>
    public PreviewFrame? TakePreviewFrame() => Interlocked.Exchange(ref _preview, null);

    public void Pause()
    {
        Session.Pause();
        IsRunning = false;
        IsLidarActive = false;
    }

    public override void DidUpdateFrame(ARSession session, ARFrame frame)
    {
        // Gli ARFrame vanno rilasciati subito (vedi ArFrameJob).
        using (frame)
        {
            var recording = Recording;
            var depthData = frame.SceneDepth;
            var depth = ArFrameReader.Depth(depthData);

            if (recording is { } rec)
            {
                WriteFrameStreams(frame, rec, depth);
            }

            // CapturedImage crea a ogni lettura un riferimento indipendente allo stesso buffer:
            // uno va all'inferenza, uno al renderer, ciascuno con il proprio ciclo di vita.
            if (frame.CapturedImage is not { } image)
            {
                return;
            }

            var displayTransform = frame.GetDisplayTransform(UIInterfaceOrientation.Portrait, Viewport);
            if (frame.CapturedImage is { } previewImage)
            {
                var preview = new PreviewFrame { Image = previewImage, Depth = depthData?.DepthMap, DisplayTransform = displayTransform };
                Interlocked.Exchange(ref _preview, preview)?.Dispose();
            }

            var job = new ArFrameJob
            {
                Image = image,
                Timestamp = frame.Timestamp,
                Depth = depth,
                Intrinsics = ArFrameReader.Intrinsics(frame.Camera),
                DisplayTransform = displayTransform,
            };
            _vision.TrySubmit(job);
        }
    }

    public override void DidFail(ARSession session, NSError error) =>
        _logger.LogError("Sessione ARKit fallita: {Error}", error.LocalizedDescription);

    public override void WasInterrupted(ARSession session) =>
        _logger.LogWarning("Sessione ARKit interrotta (app in background o camera usata da altri).");

    private void WriteFrameStreams(ARFrame frame, (ISensorSink Sink, SensorClock Clock) rec, Core.Fusion.DepthMap? depth)
    {
        var t = rec.Clock.FromUptime(frame.Timestamp);

        // Luce ambientale: 2 Hz bastano (varia lentamente), evita di riempire il DB.
        if (frame.LightEstimate is { } light && t - _lastLightReading >= 0.5)
        {
            _lastLightReading = t;
            rec.Sink.TryWrite(new SensorReading(SensorKind.AmbientLight, t, [(float)light.AmbientIntensity, (float)light.AmbientColorTemperature]));
        }

        // Riepilogo LiDAR a 5 Hz: la profondità completa per articolazione è già nel flusso PoseLandmarks3D.
        if (depth is not null && t - _lastDepthReading >= 0.2)
        {
            _lastDepthReading = t;
            rec.Sink.TryWrite(new SensorReading(SensorKind.LidarDepth, t, ArFrameReader.DepthSummary(depth)));
        }
    }
}
