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
    private ClinicalTest? _test;
    private double? _testStart;
    private double? _testEnd;

    public bool IsRecording => _session is not null;

    public ClinicalSession? Current => _session;

    public LiveMetrics? Live => _recorder?.Live;

    public double Elapsed => _clock?.Now ?? 0;

    /// <summary>Secondi dal «Via» del test; null finché la guida vocale non l'ha dato.</summary>
    public double? TestElapsed => _testStart is { } start && _clock is { } clock ? clock.Now - start : null;

    /// <summary>Segna il «Via»: da qui si misura il tempo del test (la spiegazione che lo precede non conta).</summary>
    public void MarkTestStart() => _testStart ??= _clock?.Now;

    /// <summary>
    /// Segna l'istante (secondi di sessione) in cui il paziente ha completato il compito: il tempo del test
    /// si ferma lì, non quando la registrazione viene poi chiusa.
    /// </summary>
    public void MarkTestEnd(double sessionSeconds) => _testEnd ??= sessionSeconds;

    /// <param name="test">Test standardizzato da eseguire; null per una rilevazione libera.</param>
    public async Task<ClinicalSession> StartAsync(string subjectCode, CaptureMode mode, string? notes, bool includeLocation, ClinicalTest? test = null)
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
        _test = test;
        _testStart = null;
        _testEnd = null;
        return session;
    }

    public async Task<ClinicalSession> StopAsync()
    {
        var session = _session ?? throw new InvalidOperationException("Nessuna rilevazione in corso.");
        var recorder = _recorder!;
        var duration = _clock!.Now;
        var testSeconds = _testStart is { } start ? Math.Max(0, (_testEnd ?? duration) - start) : (double?)null;

        // Prima si fermano le sorgenti (niente più scritture), poi si svuota il registratore.
        var audio = await engine.StopRecordingAsync();
        var summary = await recorder.CompleteAsync(duration, audio?.Features);

        session.EndedAt = DateTimeOffset.UtcNow;
        session.Summary = _test is { } test ? summary with { Test = ClinicalTests.Evaluate(test, summary, testSeconds) } : summary;
        await repository.CompleteSessionAsync(session);
        if (audio is not null)
        {
            await repository.AddAudioRecordingAsync(audio);
        }

        await recorder.DisposeAsync();
        _session = null;
        _recorder = null;
        _clock = null;
        _test = null;
        _testStart = null;
        _testEnd = null;
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
