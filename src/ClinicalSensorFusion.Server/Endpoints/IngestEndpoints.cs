using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Core.Telemetry;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Endpoints;

/// <summary>API usata dall'app iOS (autenticazione con chiave del dispositivo).</summary>
public static class IngestEndpoints
{
    /// <summary>Limite di richieste al minuto per dispositivo (metadati e blocchi).</summary>
    public const string RateLimit = "ingest";

    /// <summary>Limite di caricamenti audio contemporanei per dispositivo.</summary>
    public const string AudioRateLimit = "ingest-audio";

    private const int MaxChunksPerBatch = 500;

    /// <summary>Il registratore dell'app chiude i blocchi a 2.048 campioni: oltre questo valore non è un blocco reale.</summary>
    private const int MaxSamplesPerChunk = 16_384;

    public static RouteGroupBuilder MapIngest(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/ingest").WithTags("Ingestione dall'app").RequireAuthorization(Policies.Ingest);

        g.MapPut("/sessions/{id}", UpsertSessionAsync).RequireRateLimiting(RateLimit);
        // L'app invia lotti da ~1,5 MB (2 MB in base64): 16 MB lasciano margine senza accettare richieste enormi.
        g.MapPost("/sessions/{id}/chunks", UploadChunksAsync)
            .WithMetadata(new RequestSizeLimitAttribute(16L * 1024 * 1024))
            .RequireRateLimiting(RateLimit);
        // Tetto di Kestrel; il limite effettivo è AudioStorage:MaxFileBytes, applicato durante la scrittura.
        g.MapPut("/sessions/{id}/audio/{audioId}", UploadAudioAsync)
            .WithMetadata(new RequestSizeLimitAttribute(320L * 1024 * 1024))
            .RequireRateLimiting(AudioRateLimit)
            .DisableAntiforgery();
        return api;
    }

    private static async Task<Results<Ok, ValidationProblem, ProblemHttpResult>> UpsertSessionAsync(
        string id, SessionUpload upload, IngestService ingest, HttpContext http, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!AudioStorage.IsValidId(id) || upload.Id != id)
        {
            errors["id"] = [Loc.T("Identificativo di sessione non valido o diverso da quello dell'URL.", Ui.Culture)];
        }

        if (string.IsNullOrWhiteSpace(upload.SubjectCode) || upload.SubjectCode.Trim().Length > 64)
        {
            errors["subjectCode"] = [Loc.T("Codice soggetto obbligatorio (max 64).", Ui.Culture)];
        }

        if (string.IsNullOrWhiteSpace(upload.OperatorId) || string.IsNullOrWhiteSpace(upload.OperatorName))
        {
            errors["operator"] = [Loc.T("Operatore obbligatorio.", Ui.Culture)];
        }

        if (upload.Audio.Any(a => !AudioStorage.IsValidId(a.Id)))
        {
            errors["audio"] = [Loc.T("Identificativo audio non valido.", Ui.Culture)];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await ingest.UpsertSessionAsync(upload, http.User, ct) is null)
        {
            // 409 e non 403: per l'app un 403 significa "chiave revocata" e fermerebbe tutta la coda di invio.
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                detail: Loc.T("La rilevazione è già stata registrata da un altro dispositivo.", Ui.Culture));
        }

        return TypedResults.Ok();
    }

    private static async Task<Results<Ok<ChunkBatchResult>, NotFound, ValidationProblem>> UploadChunksAsync(
        string id, ChunkBatch batch, IngestService ingest, HttpContext http, CancellationToken ct)
    {
        if (batch.Chunks.Count is 0 or > MaxChunksPerBatch || batch.Chunks.Any(c => c.Payload is null || c.Payload.Length == 0 || c.Sequence < 0))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["chunks"] = [string.Format(System.Globalization.CultureInfo.InvariantCulture, Loc.T("Da 1 a {0} blocchi non vuoti.", Ui.Culture), MaxChunksPerBatch)] });
        }

        if (!await ingest.CanWriteAsync(id, http.User, ct))
        {
            return TypedResults.NotFound();
        }

        // I blocchi si salvano così come arrivano e si decodificano quando un clinico apre la rilevazione:
        // ciò che non è decodificabile entro i limiti (anche una "bomba" di decompressione) si rifiuta qui.
        foreach (var c in batch.Chunks)
        {
            if (InvalidChunk(c) is { } problem)
            {
                CsfTelemetry.Server.IngestedChunks.Add(batch.Chunks.Count, CsfTelemetry.Tag("result", "rejected"));
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["chunks"] = [$"Blocco {c.Kind} n. {c.Sequence}: {problem}"] });
            }
        }

        var inserted = await ingest.InsertChunksAsync(id, batch.Chunks, ct);
        return TypedResults.Ok(new ChunkBatchResult(batch.Chunks.Count, inserted));
    }

    private static async Task<Results<Ok, NotFound, ValidationProblem>> UploadAudioAsync(
        string id, string audioId, HttpRequest request, ClinicalDbContext db, IngestService ingest, AuditService audit,
        AudioStorage storage, TimeProvider clock, CancellationToken ct)
    {
        if (!AudioStorage.IsValidId(id) || !AudioStorage.IsValidId(audioId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["id"] = [Loc.T("Identificativo non valido.", Ui.Culture)] });
        }

        if (request.ContentLength > storage.MaxFileBytes)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [Loc.T("File audio troppo grande.", Ui.Culture)] });
        }

        if (!await ingest.CanWriteAsync(id, request.HttpContext.User, ct))
        {
            return TypedResults.NotFound();
        }

        var recording = await db.AudioRecordings.FirstOrDefaultAsync(a => a.Id == audioId && a.SessionId == id, ct);
        if (recording is null)
        {
            // I metadati arrivano con la sessione: un file senza metadati non viene accettato.
            return TypedResults.NotFound();
        }

        try
        {
            var (path, size) = await storage.SaveAsync(id, audioId, request.Body, ct);
            recording.StoragePath = path;
            recording.SizeBytes = size;
            recording.UploadedAt = clock.GetUtcNow();
            audit.Record(request.HttpContext.User, "audio.ingest", audioId);
            await db.SaveChangesAsync(ct);
            CsfTelemetry.Server.IngestedBytes.Add(size, CsfTelemetry.Tag("kind", "audio"));
            return TypedResults.Ok();
        }
        catch (InvalidDataException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [ex.Message] });
        }
    }

    private static string? InvalidChunk(ChunkUpload chunk)
    {
        if (!Enum.IsDefined(chunk.Kind))
        {
            return Loc.T("tipo di flusso sconosciuto.", Ui.Culture);
        }

        try
        {
            var samples = SensorChunkCodec.CountSamples(chunk.Payload);
            return samples != chunk.SampleCount || samples is 0 or > MaxSamplesPerChunk
                ? Loc.T("numero di campioni non valido.", Ui.Culture)
                : null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }
}
