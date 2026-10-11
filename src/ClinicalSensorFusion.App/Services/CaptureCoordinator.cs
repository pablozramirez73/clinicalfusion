using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Settings;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Caso d'uso "esegui una rilevazione": crea la sessione, collega motore di acquisizione e registratore,
/// e a fine acquisizione salva riepilogo e audio. Separato dal ViewModel per essere riusabile e perché la
/// sequenza di chiusura (ordine degli stop, flush) è delicata.
/// </summary>
public sealed class CaptureCoordinator(
    ICaptureEngine engine,
    ClinicalRepository repository,
    IAppSettings settings,
    ILoggerFactory loggerFactory)
{
    private SessionRecorder? _recorder;
    private ClinicalSession? _session;
    private SensorClock? _clock;

    public bool IsRecording => _session is not null;

    public ClinicalSession? Current => _session;

    public LiveMetrics? Live => _recorder?.Live;

    public double Elapsed => _clock?.Now ?? 0;

    public async Task<ClinicalSession> StartAsync(string subjectCode, CaptureMode mode, string? notes, bool includeLocation)
    {
        if (_session is not null)
        {
            throw new InvalidOperationException("Una rilevazione è già in corso.");
        }

        if (!settings.IsOperatorConfigured)
        {
            throw new InvalidOperationException("Configurare l'operatore nelle Impostazioni prima di iniziare.");
        }

        var session = new ClinicalSession
        {
            Id = ClinicalSession.NewId(),
            SubjectCode = subjectCode.Trim(),
            OperatorId = settings.OperatorId,
            OperatorName = settings.OperatorName,
            Mode = mode,
            StartedAt = DateTimeOffset.UtcNow,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            DeviceModel = DeviceInfo.Current.Model,
        };

        await repository.CreateSessionAsync(session);

        var recorder = new SessionRecorder(session, repository, loggerFactory.CreateLogger<SessionRecorder>());
        recorder.Start();
        // Stessa base tempi dei timestamp nativi (CoreMotion, ARKit, audio): vedi SensorClock.
        var clock = new SensorClock(() => engine.SystemUptime);

        try
        {
            await engine.StartRecordingAsync(session, recorder, clock, includeLocation);
        }
        catch
        {
            await recorder.DisposeAsync();
            throw;
        }

        _session = session;
        _recorder = recorder;
        _clock = clock;
        return session;
    }

    public async Task<ClinicalSession> StopAsync()
    {
        var session = _session ?? throw new InvalidOperationException("Nessuna rilevazione in corso.");
        var recorder = _recorder!;
        var duration = _clock!.Now;

        // Prima si fermano le sorgenti (niente più scritture), poi si svuota il registratore.
        var audio = await engine.StopRecordingAsync();
        var summary = await recorder.CompleteAsync(duration, audio?.Features);

        session.EndedAt = DateTimeOffset.UtcNow;
        session.Summary = summary;
        await repository.CompleteSessionAsync(session);
        if (audio is not null)
        {
            await repository.AddAudioRecordingAsync(audio);
        }

        await recorder.DisposeAsync();
        _session = null;
        _recorder = null;
        _clock = null;
        return session;
    }

    /// <summary>Elimina una rilevazione e i relativi file audio.</summary>
    public async Task DeleteAsync(string sessionId)
    {
        var audio = await repository.ListAudioRecordingsAsync(sessionId);
        await repository.DeleteSessionAsync(sessionId, settings.OperatorId);
        foreach (var a in audio)
        {
            var dir = AppPaths.AudioDirectory(a.Id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
