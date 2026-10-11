using System.Security.Claims;
using System.Text.Json;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Services;

/// <summary>
/// Ricezione dei dati dall'app. Tutte le operazioni sono idempotenti: l'app ritenta dopo un'interruzione e
/// il risultato finale non cambia (upsert della sessione, blocchi "insert se assente", audio sovrascritto).
/// </summary>
public sealed class IngestService(ClinicalDbContext db, AuditService audit, TimeProvider clock)
{
    public async Task<SessionEntity> UpsertSessionAsync(SessionUpload upload, ClaimsPrincipal device, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var code = Patient.NormalizeCode(upload.SubjectCode);

        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Code == code, ct);
        if (patient is null)
        {
            // Codice mai visto: si crea un'anagrafica "segnaposto" da completare nella dashboard.
            patient = new Patient { Id = Guid.CreateVersion7(), Code = code, IsPlaceholder = true, CreatedAt = now, UpdatedAt = now };
            db.Patients.Add(patient);
            audit.Record(device, "patient.autocreate", code);
        }

        var session = await db.Sessions
            .Include(s => s.Audio)
            .Include(s => s.Analyses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == upload.Id, ct);

        if (session is null)
        {
            session = new SessionEntity
            {
                Id = upload.Id,
                SubjectCode = code,
                OperatorId = upload.OperatorId,
                OperatorName = upload.OperatorName,
                ReceivedAt = now,
            };
            db.Sessions.Add(session);
        }

        session.Patient = patient;
        session.SubjectCode = code;
        session.OperatorId = upload.OperatorId;
        session.OperatorName = upload.OperatorName;
        session.Mode = upload.Mode;
        session.StartedAt = upload.StartedAt;
        session.EndedAt = upload.EndedAt;
        session.Notes = upload.Notes;
        session.DeviceModel = upload.DeviceModel;
        session.UpdatedAt = now;

        if (upload.Summary is { } summary)
        {
            session.SummaryJson = JsonSerializer.Serialize(summary, CsfJsonContext.Default.SessionSummary);
            session.DurationSeconds = summary.DurationSeconds;
            session.LidarCoverage = summary.LidarCoverage;
            session.TremorFrequencyHz = summary.Tremor is { } t && double.IsFinite(t.DominantFrequencyHz) ? t.DominantFrequencyHz : null;
            session.TremorRmsMs2 = summary.Tremor?.BandRmsMs2;
            session.Steps = summary.Gait?.Steps;
        }

        foreach (var a in upload.Audio)
        {
            var existing = session.Audio.FirstOrDefault(x => x.Id == a.Id);
            if (existing is null)
            {
                existing = new AudioRecordingEntity { Id = a.Id, SessionId = session.Id };
                session.Audio.Add(existing);
            }

            existing.CreatedAt = a.CreatedAt;
            existing.DurationSeconds = a.DurationSeconds;
            existing.FeaturesJson = a.Features is null ? null : JsonSerializer.Serialize(a.Features, CsfJsonContext.Default.AudioFeatures);
        }

        foreach (var a in upload.Analyses.Where(a => session.Analyses.All(x => x.Id != a.Id)))
        {
            session.Analyses.Add(new AiAnalysisEntity
            {
                Id = a.Id,
                SessionId = session.Id,
                CreatedAt = a.CreatedAt,
                Model = a.Model,
                Language = a.Language,
                OperatorId = a.OperatorId,
                Prompt = a.Prompt,
                Response = a.Response,
            });
        }

        audit.Record(device, "session.ingest", session.Id);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>Inserisce i blocchi non ancora presenti. Restituisce quanti sono stati effettivamente inseriti.</summary>
    public async Task<int> InsertChunksAsync(string sessionId, IReadOnlyList<ChunkUpload> chunks, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = 0;
        foreach (var c in chunks)
        {
            // ON CONFLICT DO NOTHING: idempotenza a livello di database, sicura anche con invii concorrenti.
            inserted += await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO sensor_chunks ("SessionId", "Kind", "Sequence", "StartTime", "EndTime", "SampleCount", "Payload")
                VALUES ({sessionId}, {(int)c.Kind}, {c.Sequence}, {c.StartTime}, {c.EndTime}, {c.SampleCount}, {c.Payload})
                ON CONFLICT DO NOTHING
                """, ct);
        }

        await tx.CommitAsync(ct);
        return inserted;
    }
}
