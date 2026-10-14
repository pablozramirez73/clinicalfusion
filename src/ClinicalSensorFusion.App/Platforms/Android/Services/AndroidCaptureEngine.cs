using ClinicalSensorFusion.Core.Localization;
using Android.OS;
using ClinicalSensorFusion.App.Platforms.Android.Sensors;
using ClinicalSensorFusion.App.Platforms.Android.Vision;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Sensors;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Platforms.Android.Services;

/// <summary>
/// Implementazione Android di <see cref="ICaptureEngine"/>: CameraX + MediaPipe in anteprima, e in
/// registrazione anche i sensori di movimento, barometro, contapassi, luce, prossimità, stato del
/// dispositivo, microfono e (opzionale) GPS.
/// </summary>
internal sealed class AndroidCaptureEngine : ICaptureEngine, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly VisionPipeline _vision;
    private SensorSourceGroup? _sources;
    private AudioCaptureSource? _audio;

    public AndroidCaptureEngine(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AndroidCaptureEngine>();
        _vision = new VisionPipeline(loggerFactory.CreateLogger<VisionPipeline>());
        _vision.OverlayUpdated += (s, e) => OverlayUpdated?.Invoke(this, e);
        Camera = new CameraController(_vision, _logger);
    }

    public event EventHandler<OverlayFrame>? OverlayUpdated;

    /// <summary>Usato dall'handler dell'anteprima.</summary>
    public CameraController Camera { get; }

    // Nessun sensore di profondità metrica: l'interruttore del LiDAR non compare e gli angoli usano la stima 3D.
    public bool IsLidarSupported => false;

    public bool UseLidar { get; set; }


    public bool IsRecording { get; private set; }

    public double SystemUptime => SystemClock.ElapsedRealtimeNanos() / 1e9;

    public IReadOnlyList<SensorStatus> GetSensorStatus()
    {
        var mode = Camera.Mode;
        var list = new List<SensorStatus>
        {
            new(Loc.T("Fotocamera posteriore"), true, Camera.IsRunning),
            new($"MediaPipe {ModeModel(mode)} · {(_vision.IsGpu ? "GPU" : "CPU")}", _vision.IsReady, Camera.IsRunning && _vision.IsReady),
        };

        if (IsRecording)
        {
            list.AddRange(_sources!.Members.Select(s => new SensorStatus(s.DisplayName, s.IsAvailable, s.IsAvailable)));
        }

        return list;
    }

    public async Task<bool> RequestPermissionsAsync(bool includeLocation)
    {
        var camera = await Permissions.RequestAsync<Permissions.Camera>();
        var microphone = await Permissions.RequestAsync<Permissions.Microphone>();
        // Contapassi: se negato la rilevazione funziona lo stesso, senza passi.
        await Permissions.RequestAsync<ActivityRecognitionPermission>();
        if (includeLocation)
        {
            await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        return camera == PermissionStatus.Granted && microphone == PermissionStatus.Granted;
    }

    public async Task StartPreviewAsync(CaptureMode mode)
    {
        await Camera.StartAsync(mode);
        if (_vision.LastError is { } error)
        {
            _logger.LogWarning("Visione non disponibile: {Error}", error);
        }
    }

    public async Task StopPreviewAsync()
    {
        if (IsRecording)
        {
            await StopRecordingAsync();
        }

        await Camera.StopAsync();
    }

    public async Task StartRecordingAsync(ClinicalSession session, ISensorSink sink, SensorClock clock, bool includeLocation)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException(Loc.T("Registrazione già in corso."));
        }

        if (!Camera.IsRunning || Camera.Mode != session.Mode)
        {
            await Camera.StartAsync(session.Mode);
        }

        List<ISensorSource> sources =
        [
            new MotionSensorSource(clock),
            new BarometerSensorSource(clock),
            new PedometerSensorSource(clock),
            new LightSensorSource(clock),
            new SystemSensorSource(clock),
        ];
        if (includeLocation)
        {
            sources.Add(new LocationSensorSource(clock));
        }

        var audio = new AudioCaptureSource(clock, session.Id, _loggerFactory.CreateLogger<AudioCaptureSource>());
        sources.Add(audio);

        // Se una sorgente non parte, il gruppo ferma e rilascia quelle già avviate prima di rilanciare.
        var group = new SensorSourceGroup(sources, _logger);
        await group.StartAsync(sink, CancellationToken.None);
        _sources = group;
        _audio = audio;

        _vision.Recording = (sink, clock);
        IsRecording = true;
    }

    public async Task<AudioRecording?> StopRecordingAsync()
    {
        if (!IsRecording)
        {
            return null;
        }

        _vision.Recording = null;
        await _sources!.StopAsync();
        var audio = _audio?.Complete();
        _sources.Dispose();
        _sources = null;
        _audio = null;
        IsRecording = false;
        return audio;
    }

    public void Dispose()
    {
        Camera.Dispose();
        _vision.Dispose();
    }

    private static string ModeModel(CaptureMode mode) => mode switch
    {
        CaptureMode.BodyLidar => "Pose",
        CaptureMode.HandsLidar => "Hands",
        _ => string.Empty,
    };

    /// <summary>Permesso "Attività fisica", richiesto da Android per leggere il contapassi.</summary>
    private sealed class ActivityRecognitionPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            [(global::Android.Manifest.Permission.ActivityRecognition, true)];
    }
}
