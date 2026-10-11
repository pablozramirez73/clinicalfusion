using ClinicalSensorFusion.Core.Sync;
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
    private const int MaxChunksPerBatch = 500;

    public static RouteGroupBuilder MapIngest(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/ingest").WithTags("Ingestione dall'app").RequireAuthorization(Policies.Ingest);

        g.MapPut("/sessions/{id}", UpsertSessionAsync);
        g.MapPost("/sessions/{id}/chunks", UploadChunksAsync)
            .WithMetadata(new RequestSizeLimitAttribute(64L * 1024 * 1024));
        g.MapPut("/sessions/{id}/audio/{audioId}", UploadAudioAsync)
            .WithMetadata(new RequestSizeLimitAttribute(320L * 1024 * 1024))
            .DisableAntiforgery();
        return api;
    }

    private static async Task<Results<Ok, ValidationProblem>> UpsertSessionAsync(
        string id, SessionUpload upload, IngestService ingest, HttpContext http, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!AudioStorage.IsValidId(id) || upload.Id != id)
        {
            errors["id"] = ["Identificativo di sessione non valido o diverso da quello dell'URL."];
        }

        if (string.IsNullOrWhiteSpace(upload.SubjectCode) || upload.SubjectCode.Trim().Length > 64)
        {
            errors["subjectCode"] = ["Codice soggetto obbligatorio (max 64)."];
        }

        if (string.IsNullOrWhiteSpace(upload.OperatorId) || string.IsNullOrWhiteSpace(upload.OperatorName))
        {
            errors["operator"] = ["Operatore obbligatorio."];
        }

        if (upload.Audio.Any(a => !AudioStorage.IsValidId(a.Id)))
        {
            errors["audio"] = ["Identificativo audio non valido."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        await ingest.UpsertSessionAsync(upload, http.User, ct);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok<ChunkBatchResult>, NotFound, ValidationProblem>> UploadChunksAsync(
        string id, ChunkBatch batch, ClinicalDbContext db, IngestService ingest, CancellationToken ct)
    {
        if (batch.Chunks.Count is 0 or > MaxChunksPerBatch || batch.Chunks.Any(c => c.Payload is null || c.Payload.Length == 0 || c.Sequence < 0))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["chunks"] = [$"Da 1 a {MaxChunksPerBatch} blocchi non vuoti."] });
        }

        if (!await db.Sessions.AnyAsync(s => s.Id == id, ct))
        {
            return TypedResults.NotFound();
        }

        var inserted = await ingest.InsertChunksAsync(id, batch.Chunks, ct);
        return TypedResults.Ok(new ChunkBatchResult(batch.Chunks.Count, inserted));
    }

    private static async Task<Results<Ok, NotFound, ValidationProblem>> UploadAudioAsync(
        string id, string audioId, HttpRequest request, ClinicalDbContext db, AudioStorage storage, TimeProvider clock, CancellationToken ct)
    {
        if (!AudioStorage.IsValidId(id) || !AudioStorage.IsValidId(audioId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["Identificativo non valido."] });
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
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok();
        }
        catch (InvalidDataException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [ex.Message] });
        }
    }
}
