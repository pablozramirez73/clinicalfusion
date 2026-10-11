using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ClinicalSensorFusion.Core.Sync;

/// <summary>
/// Client HTTP dell'API di ingestione del server. Tutte le chiamate sono idempotenti (PUT per sessione e
/// audio, inserimento "se assente" per i blocchi): un invio interrotto si ripete senza duplicati.
/// L'autenticazione (header <c>X-Api-Key</c>) è a carico dell'HttpClient configurato dal chiamante.
/// </summary>
public sealed class ClinicalSyncClient(HttpClient http)
{
    public const string ApiKeyHeader = "X-Api-Key";

    public async Task UploadSessionAsync(Uri server, SessionUpload session, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync(
            new Uri(server, $"api/v1/ingest/sessions/{Uri.EscapeDataString(session.Id)}"),
            session,
            SyncJsonContext.Default.SessionUpload,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<ChunkBatchResult> UploadChunksAsync(Uri server, string sessionId, IReadOnlyList<ChunkUpload> chunks, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            new Uri(server, $"api/v1/ingest/sessions/{Uri.EscapeDataString(sessionId)}/chunks"),
            new ChunkBatch([.. chunks]),
            SyncJsonContext.Default.ChunkBatch,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.ChunkBatchResult, ct).ConfigureAwait(false)
            ?? new ChunkBatchResult(chunks.Count, 0);
    }

    /// <summary>Invia il file CAF in streaming (può superare i 30 MB: niente buffer in memoria).</summary>
    public async Task UploadAudioAsync(Uri server, string sessionId, string audioId, Stream caf, CancellationToken ct = default)
    {
        using var content = new StreamContent(caf);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/x-caf");
        using var response = await http.PutAsync(
            new Uri(server, $"api/v1/ingest/sessions/{Uri.EscapeDataString(sessionId)}/audio/{Uri.EscapeDataString(audioId)}"),
            content,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new SyncException((int)response.StatusCode, body.Length > 500 ? body[..500] : body);
    }
}

public sealed class SyncException(int statusCode, string detail)
    : Exception($"Il server ha risposto {statusCode}: {detail}")
{
    public int StatusCode { get; } = statusCode;

    /// <summary>401/403: chiave del dispositivo errata o revocata. Ritentare non serve.</summary>
    public bool IsAuthenticationError => StatusCode is 401 or 403;
}
