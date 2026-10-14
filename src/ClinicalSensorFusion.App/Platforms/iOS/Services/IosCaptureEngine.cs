using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.App.Platforms.iOS.Sensors;
using ClinicalSensorFusion.App.Platforms.iOS.Vision;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Sensors;
using Foundation;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Platforms.iOS.Services;

/// <summary>
/// Implementazione iOS di <see cref="ICaptureEngine"/>: ARKit + MediaPipe in anteprima, e in registrazione
/// anche CoreMotion, barometro, pedometro, prossimità, stato del dispositivo, microfono e (opzionale) GPS.
/// </summary>
internal sealed class IosCaptureEngine : ICaptureEngine, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly VisionPipeline _vision;
    private readonly List<ISensorSource> _sources = [];
    private AudioCaptureSource? _audio;

    public IosCaptureEngine(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<IosCaptureEngine>();
        _vision = new VisionPipeline(loggerFactory.CreateLogger<VisionPipeline>());
        _vision.OverlayUpdated += (s, e) => OverlayUpdated?.Invoke(this, e);
        Ar = new ArCaptureController(_vision, _logger);
    }

    public event EventHandler<OverlayFrame>? OverlayUpdated;

    /// <summary>Usato dall'handler dell'anteprima Metal.</summary>
    public ArCaptureController Ar { get; }

    public bool IsLidarSupported => ArCaptureController.LidarSupported;

    public bool UseLidar
    {
        get => Ar.UseLidar;
        set => Ar.UseLidar = value;
    }

    public bool IsFaceTrackingSupported => ArCaptureController.FaceTrackingSupported;

    public bool IsRecording { get; private set; }

    public double SystemUptime => NSProcessInfo.ProcessInfo.SystemUptime;

    public IReadOnlyList<SensorStatus> GetSensorStatus()
    {
        var mode = Ar.Mode;
        var list = new List<SensorStatus>
        {
            new(mode == CaptureMode.FaceTrueDepth ? Loc.T("Fotocamera TrueDepth") : Loc.T("Fotocamera posteriore"), true, Ar.IsRunning),
            new("LiDAR", IsLidarSupported, Ar.IsRunning && Ar.IsLidarActive),
            new($"MediaPipe {ModeModel(mode)}", _vision.IsReady, Ar.IsRunning && _vision.IsReady),
        };

        if (IsRecording)
        {
            list.AddRange(_sources.Select(s => new SensorStatus(s.DisplayName, s.IsAvailable, s.IsAvailable)));
        }

        return list;
    }

    public async Task<bool> RequestPermissionsAsync(bool includeLocation)
    {
        var camera = await Permissions.RequestAsync<Permissions.Camera>();
        var microphone = await Permissions.RequestAsync<Permissions.Microphone>();
        if (includeLocation)
        {
            await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        // CoreMotion chiede il permesso "Movimento e fitness" al primo utilizzo del pedometro.
        return camera == PermissionStatus.Granted && microphone == PermissionStatus.Granted;
    }

    public Task StartPreviewAsync(CaptureMode mode)
    {
        Ar.Run(mode);
        if (_vision.LastError is { } error)
        {
            _logger.LogWarning("Visione non disponibile: {Error}", error);
        }

        return Task.CompletedTask;
    }

    public async Task StopPreviewAsync()
    {
        if (IsRecording)
        {
            await StopRecordingAsync();
        }

        Ar.Pause();
    }

    public async Task StartRecordingAsync(ClinicalSession session, ISensorSink sink, SensorClock clock, bool includeLocation)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException(Loc.T("Registrazione già in corso."));
        }

        if (!Ar.IsRunning || Ar.Mode != session.Mode)
        {
            Ar.Run(session.Mode);
        }

        _sources.Clear();
        _sources.Add(new MotionSensorSource(clock));
        _sources.Add(new BarometerSensorSource(clock));
        _sources.Add(new PedometerSensorSource(clock));
        _sources.Add(new SystemSensorSource(clock));
        if (includeLocation)
        {
            _sources.Add(new LocationSensorSource(clock));
        }

        _audio = new AudioCaptureSource(clock, session.Id, _loggerFactory.CreateLogger<AudioCaptureSource>());
        _sources.Add(_audio);

        var started = new List<ISensorSource>();
        try
        {
            foreach (var source in _sources.Where(s => s.IsAvailable))
            {
                await source.StartAsync(sink, CancellationToken.None);
                started.Add(source);
            }
        }
        catch
        {
            // Avvio parziale: si ferma quanto già partito per non lasciare sensori accesi.
            foreach (var source in started)
            {
                await SafeStopAsync(source);
            }

            foreach (var source in _sources)
            {
                source.Dispose();
            }

            _sources.Clear();
            _audio = null;
            throw;
        }

        Ar.Recording = (sink, clock);
        _vision.Recording = (sink, clock);
        IsRecording = true;
    }

    public async Task<AudioRecording?> StopRecordingAsync()
    {
        if (!IsRecording)
        {
            return null;
        }

        Ar.Recording = null;
        _vision.Recording = null;
        foreach (var source in _sources)
        {
            await SafeStopAsync(source);
        }

        var audio = _audio?.Complete();
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _sources.Clear();
        _audio = null;
        IsRecording = false;
        return audio;
    }

    public void Dispose()
    {
        Ar.Pause();
        _vision.Dispose();
        Ar.Dispose();
    }

    private async Task SafeStopAsync(ISensorSource source)
    {
        try
        {
            await source.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Arresto di {Source} fallito.", source.DisplayName);
        }
    }

    private static string ModeModel(CaptureMode mode) => mode switch
    {
        CaptureMode.BodyLidar => "Pose",
        CaptureMode.HandsLidar => "Hands",
        CaptureMode.FaceTrueDepth => "Face",
        _ => string.Empty,
    };
}
