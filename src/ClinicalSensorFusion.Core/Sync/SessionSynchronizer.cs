using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Sync;

/// <summary>Esito della sincronizzazione.</summary>
public sealed record SyncReport(int Sessions, int Chunks, int AudioFiles, IReadOnlyList<string> Errors);

/// <summary>
/// Invia al server le rilevazioni concluse e non ancora sincronizzate: metadati e riepilogo, blocchi dei
/// sensori (a lotti), file audio, analisi IA. Solo a invio completo la sessione è marcata come sincronizzata,
/// quindi un'interruzione (rete, app in background) si recupera al tentativo successivo.
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
        int sessions = 0, chunks = 0, audioFiles = 0;
        var errors = new List<string>();

        foreach (var session in pending)
        {
            try
            {
                var (c, a) = await SyncSessionAsync(server, session, audioPathResolver, ct).ConfigureAwait(false);
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

        return new SyncReport(sessions, chunks, audioFiles, errors);
    }

    private async Task<(int Chunks, int Audio)> SyncSessionAsync(
        Uri server, ClinicalSession session, Func<AudioRecording, string> audioPathResolver, CancellationToken ct)
    {
        var audio = await repository.ListAudioRecordingsAsync(session.Id, ct).ConfigureAwait(false);
        var analyses = await repository.ListAnalysesAsync(session.Id, ct).ConfigureAwait(false);

        await client.UploadSessionAsync(server, new SessionUpload(
            session.Id, session.SubjectCode, session.OperatorId, session.OperatorName, session.Mode,
            session.StartedAt, session.EndedAt, session.Notes, session.DeviceModel, session.Summary,
            [.. audio.Select(a => new AudioUpload(a.Id, a.CreatedAt, a.DurationSeconds, a.Features))],
            [.. analyses.Select(a => new AnalysisUpload(a.Id, a.CreatedAt, a.Model, a.Language, a.OperatorId, a.Prompt, a.Response))]),
            ct).ConfigureAwait(false);

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
        foreach (var recording in audio)
        {
            var path = audioPathResolver(recording);
            if (!File.Exists(path))
            {
                logger.LogWarning("File audio {AudioId} assente: si inviano solo i metadati.", recording.Id);
                continue;
            }

            await using var stream = File.OpenRead(path);
            await client.UploadAudioAsync(server, session.Id, recording.Id, stream, ct).ConfigureAwait(false);
            uploadedAudio++;
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
            batch.Clear();
            batchBytes = 0;
            return count;
        }
    }
}
