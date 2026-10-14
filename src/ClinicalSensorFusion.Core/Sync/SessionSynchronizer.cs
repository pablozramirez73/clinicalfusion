using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Sync;

/// <summary>Esito della sincronizzazione.</summary>
public sealed record SyncReport(int Sessions, int Chunks, int AudioFiles, IReadOnlyList<string> Errors);

/// <summary>
/// Invia al server le rilevazioni concluse e non ancora sincronizzate: metadati e riepilogo, blocchi dei
/// sensori (a lotti), file audio, analisi IA. Solo a invio completo la sessione è marcata come sincronizzata,
/// quindi un'interruzione (rete, app in background) si recupera al tentativo successivo. Un file audio
/// mancante sul dispositivo conta come invio incompleto: viene segnalato, non ignorato.
/// </summary>
public sealed class SessionSynchronizer(
    ClinicalRepository repository,
    ClinicalSyncClient client,
    ILogger<SessionSynchronizer> logger)
{
    /// <summary>~1,5 MB di payload per richiesta: abbastanza grande da ridurre i round-trip, abbastanza piccolo per il 4G.</summary>
    public int MaxBatchBytes { get; init; } = 1_500_000;

    /// <param name="audioPathResolver">Percorso assoluto del file CAF di una registrazione.</param>
    public async Task<SyncReport> SyncPendingAsync(Uri server, Func<AudioRecording, string> audioPathResolver, CancellationToken ct = default)
    {
        var pending = await repository.ListUnsyncedSessionsAsync(ct).ConfigureAwait(false);
        CsfTelemetry.Sync.PendingSessions.Record(pending.Count);
        int sessions = 0, chunks = 0, audioFiles = 0;
        var errors = new List<string>();

        foreach (var session in pending)
        {
            try
            {
                var (c, a) = await UploadAsync(server, session, audioPathResolver, ct).ConfigureAwait(false);
                sessions++;
                chunks += c;
                audioFiles += a;
            }
            catch (SyncException ex) when (ex.IsAuthenticationError)
            {
                // Inutile proseguire con le altre sessioni: fallirebbero tutte allo stesso modo.
                errors.Add(ex.Message);
                break;
            }
            catch (Exception ex) when (ex is SyncException or HttpRequestException or IOException)
            {
                logger.LogWarning(ex, "Sincronizzazione della sessione {SessionId} fallita.", session.Id);
                errors.Add($"{session.SubjectCode}: {ex.Message}");
            }
        }

        CsfTelemetry.Sync.PendingSessions.Record(pending.Count - sessions);
        return new SyncReport(sessions, chunks, audioFiles, errors);
    }

    /// <summary>
    /// Invia una sola rilevazione, anche se già sincronizzata: l'API è idempotente, quindi il reinvio serve
    /// ad aggiornare il server (per esempio con le analisi IA aggiunte dopo il primo invio).
    /// </summary>
    public async Task<SyncReport> SyncSessionAsync(Uri server, string sessionId, Func<AudioRecording, string> audioPathResolver, CancellationToken ct = default)
    {
        var session = await repository.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return new SyncReport(0, 0, 0, [Loc.T("Rilevazione non trovata.")]);
        }

        if (session.EndedAt is null)
        {
            return new SyncReport(0, 0, 0, [Loc.T("La rilevazione è incompleta: non può essere inviata.")]);
        }

        try
        {
            var (chunks, audio) = await UploadAsync(server, session, audioPathResolver, ct).ConfigureAwait(false);
            return new SyncReport(1, chunks, audio, []);
        }
        catch (Exception ex) when (ex is SyncException or HttpRequestException or IOException)
        {
            logger.LogWarning(ex, "Invio della sessione {SessionId} fallito.", session.Id);
            return new SyncReport(0, 0, 0, [ex.Message]);
        }
    }

    /// <summary>
    /// Il documento JSON della rilevazione così come viene inviato al server (metadati, riepilogo, indice
    /// dell'audio, analisi). Pubblico perché l'app ne controlla l'integrità prima dell'invio.
    /// </summary>
    public async Task<SessionUpload> BuildUploadAsync(ClinicalSession session, CancellationToken ct = default)
    {
        var audio = await repository.ListAudioRecordingsAsync(session.Id, ct).ConfigureAwait(false);
        var analyses = await repository.ListAnalysesAsync(session.Id, ct).ConfigureAwait(false);
        return new SessionUpload(
            session.Id, session.SubjectCode, session.OperatorId, session.OperatorName, session.Mode,
            session.StartedAt, session.EndedAt, session.Notes, session.DeviceModel, session.Summary,
            [.. audio.Select(a => new AudioUpload(a.Id, a.CreatedAt, a.DurationSeconds, a.Features))],
            [.. analyses.Select(a => new AnalysisUpload(a.Id, a.CreatedAt, a.Model, a.Language, a.OperatorId, a.Prompt, a.Response))]);
    }

    /// <summary>Controllo del payload JSON di una rilevazione e dei dati dei sensori che lo accompagnano.</summary>
    public async Task<PayloadReport> InspectPayloadAsync(ClinicalSession session, CancellationToken ct = default)
    {
        var upload = await BuildUploadAsync(session, ct).ConfigureAwait(false);
        var (chunks, samples, bytes) = await repository.GetChunkStatsAsync(session.Id, ct).ConfigureAwait(false);
        return PayloadReport.Inspect(upload, new ChunkStats(chunks, samples, bytes));
    }

    private async Task<(int Chunks, int Audio)> UploadAsync(
        Uri server, ClinicalSession session, Func<AudioRecording, string> audioPathResolver, CancellationToken ct)
    {
        using var operation = CsfTelemetry.StartOperation(logger, "sync.session", CsfTelemetry.Sync.SessionDuration);
        // L'identificativo è casuale (non è un dato del paziente) e lega tra loro le righe di log dell'invio.
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["SessionId"] = session.Id });
        try
        {
            var result = await UploadCoreAsync(server, session, audioPathResolver, ct).ConfigureAwait(false);
            operation.Succeed();
            return result;
        }
        catch (Exception ex)
        {
            operation.Fail(ex, cancelledByCaller: ct.IsCancellationRequested);
            throw;
        }
    }

    private async Task<(int Chunks, int Audio)> UploadCoreAsync(
        Uri server, ClinicalSession session, Func<AudioRecording, string> audioPathResolver, CancellationToken ct)
    {
        var audio = await repository.ListAudioRecordingsAsync(session.Id, ct).ConfigureAwait(false);
        await client.UploadSessionAsync(server, await BuildUploadAsync(session, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        var sent = 0;
        var batch = new List<ChunkUpload>();
        var batchBytes = 0;
        await foreach (var chunk in repository.ReadRawChunksAsync(session.Id, ct).ConfigureAwait(false))
        {
            batch.Add(new ChunkUpload(chunk.Kind, chunk.Sequence, chunk.StartTime, chunk.EndTime, chunk.SampleCount, chunk.Payload));
            batchBytes += chunk.Payload.Length;
            if (batchBytes >= MaxBatchBytes)
            {
                sent += await FlushAsync().ConfigureAwait(false);
            }
        }

        sent += await FlushAsync().ConfigureAwait(false);

        var uploadedAudio = 0;
        var missingAudio = 0;
        foreach (var recording in audio)
        {
            var path = audioPathResolver(recording);
            if (!File.Exists(path))
            {
                // Si prosegue con gli altri file, ma la rilevazione non risulterà sincronizzata.
                logger.LogWarning("File audio {AudioId} assente sul dispositivo.", recording.Id);
                CsfTelemetry.Sync.AudioFiles.Add(1, CsfTelemetry.Tag("result", "missing"));
                missingAudio++;
                continue;
            }

            await using var stream = File.OpenRead(path);
            var audioBytes = stream.Length; // letta prima: l'invio chiude lo stream
            await client.UploadAudioAsync(server, session.Id, recording.Id, stream, ct).ConfigureAwait(false);
            CsfTelemetry.Sync.AudioFiles.Add(1, CsfTelemetry.Tag("result", "sent"));
            CsfTelemetry.Sync.Bytes.Add(audioBytes, CsfTelemetry.Tag("kind", "audio"));
            uploadedAudio++;
        }

        if (missingAudio > 0)
        {
            // Mai "sincronizzata" con audio mancante: l'operatore deve saperlo, e sul server la registrazione
            // resta senza file. Dati e altri audio sono già arrivati (invio idempotente).
            throw new FileNotFoundException(missingAudio == 1
                ? Loc.T("Un file audio non è più sul dispositivo: la rilevazione è stata inviata senza quell'audio e resta da sincronizzare.")
                : Loc.F("{0} file audio non sono più sul dispositivo: la rilevazione è stata inviata senza quegli audio e resta da sincronizzare.", missingAudio));
        }

        await repository.MarkSyncedAsync(session.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return (sent, uploadedAudio);

        async Task<int> FlushAsync()
        {
            if (batch.Count == 0)
            {
                return 0;
            }

            await client.UploadChunksAsync(server, session.Id, batch, ct).ConfigureAwait(false);
            var count = batch.Count;
            CsfTelemetry.Sync.Chunks.Add(count);
            CsfTelemetry.Sync.Bytes.Add(batchBytes, CsfTelemetry.Tag("kind", "chunks"));
            batch.Clear();
            batchBytes = 0;
            return count;
        }
    }
}
