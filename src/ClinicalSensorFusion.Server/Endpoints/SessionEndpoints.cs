using System.Text.Json;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Endpoints;

public sealed record SessionListItem(
    string Id, Guid PatientId, string PatientCode, string? PatientName, CaptureMode Mode, DateTimeOffset StartedAt,
    double? DurationSeconds, string OperatorName, double? TremorFrequencyHz, double? LidarCoverage, int AudioCount, int AnalysisCount);

public sealed record AudioItem(string Id, DateTimeOffset CreatedAt, double DurationSeconds, JsonElement? Features, bool Available, long? SizeBytes);

public sealed record AnalysisItem(string Id, DateTimeOffset CreatedAt, string Model, string Language, string OperatorId, string Response);

public sealed record SessionDetail(
    string Id, Guid PatientId, string PatientCode, string? PatientName, CaptureMode Mode, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string OperatorId, string OperatorName, string? Notes, string? DeviceModel, JsonElement? Summary,
    IReadOnlyList<SummaryRow> Report, IReadOnlyDictionary<SensorKind, int> Streams,
    IReadOnlyList<AudioItem> Audio, IReadOnlyList<AnalysisItem> Analyses);

/// <summary>Un punto della serie storica di un paziente (una rilevazione).</summary>
public sealed record TrendPoint(
    string SessionId, DateTimeOffset StartedAt, CaptureMode Mode, double? DurationSeconds,
    IReadOnlyDictionary<Joint, JointRange> Rom, double? TremorFrequencyHz, double? TremorRmsMs2,
    double? CadenceStepsPerMinute, long? Steps, double? AudioRmsDbfs);

public static class SessionEndpoints
{
    public static RouteGroupBuilder MapSessions(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Rilevazioni").RequireAuthorization(Policies.Clinician);

        g.MapGet("/sessions", ListAsync);
        g.MapGet("/patients/{patientId:guid}/sessions", ListForPatientAsync);
        g.MapGet("/patients/{patientId:guid}/trends", TrendsAsync);
        g.MapGet("/sessions/{id}", GetAsync);
        g.MapDelete("/sessions/{id}", DeleteAsync);
        g.MapGet("/sessions/{id}/pose", PoseAsync);
        g.MapGet("/sessions/{id}/spectrogram", SpectrogramAsync);
        g.MapGet("/sessions/{id}/streams/{kind}", StreamAsync);
        g.MapGet("/sessions/{id}/audio/{audioId}.wav", AudioAsync);
        return api;
    }

    private static IQueryable<SessionListItem> Project(IQueryable<SessionEntity> q) => q.Select(s => new SessionListItem(
        s.Id, s.PatientId, s.Patient!.Code,
        s.Patient.LastName == null ? null : s.Patient.LastName + " " + s.Patient.FirstName,
        s.Mode, s.StartedAt, s.DurationSeconds, s.OperatorName, s.TremorFrequencyHz, s.LidarCoverage,
        s.Audio.Count, s.Analyses.Count));

    private static async Task<Ok<List<SessionListItem>>> ListAsync(
        ClinicalDbContext db, DateTimeOffset? from, DateTimeOffset? to, CaptureMode? mode, int take = 50, CancellationToken ct = default)
    {
        var q = db.Sessions.AsNoTracking();
        if (from is { } f)
        {
            q = q.Where(s => s.StartedAt >= f);
        }

        if (to is { } t)
        {
            q = q.Where(s => s.StartedAt < t);
        }

        if (mode is { } m)
        {
            q = q.Where(s => s.Mode == m);
        }

        return TypedResults.Ok(await Project(q.OrderByDescending(s => s.StartedAt).Take(Math.Clamp(take, 1, 500))).ToListAsync(ct));
    }

    private static async Task<Ok<List<SessionListItem>>> ListForPatientAsync(Guid patientId, ClinicalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await Project(db.Sessions.AsNoTracking().Where(s => s.PatientId == patientId).OrderByDescending(s => s.StartedAt)).ToListAsync(ct));

    private static async Task<Ok<List<TrendPoint>>> TrendsAsync(Guid patientId, ClinicalDbContext db, CancellationToken ct)
    {
        var rows = await db.Sessions.AsNoTracking()
            .Where(s => s.PatientId == patientId && s.SummaryJson != null)
            .OrderBy(s => s.StartedAt)
            .Select(s => new { s.Id, s.StartedAt, s.Mode, s.SummaryJson })
            .ToListAsync(ct);

        var points = new List<TrendPoint>(rows.Count);
        foreach (var r in rows)
        {
            var summary = JsonSerializer.Deserialize(r.SummaryJson!, CsfJsonContext.Default.SessionSummary);
            if (summary is null)
            {
                continue;
            }

            points.Add(new TrendPoint(
                r.Id, r.StartedAt, r.Mode, summary.DurationSeconds, summary.JointRanges,
                summary.Tremor is { } t && double.IsFinite(t.DominantFrequencyHz) ? t.DominantFrequencyHz : null,
                summary.Tremor?.BandRmsMs2, summary.Gait?.CadenceStepsPerMinute, summary.Gait?.Steps, summary.Audio?.RmsDbfs));
        }

        return TypedResults.Ok(points);
    }

    private static async Task<Results<Ok<SessionDetail>, NotFound>> GetAsync(
        string id, ClinicalDbContext db, AuditService audit, AudioStorage storage, HttpContext http, CancellationToken ct)
    {
        var s = await db.Sessions.AsNoTracking()
            .Include(x => x.Patient)
            .Include(x => x.Audio)
            .Include(x => x.Analyses)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
        {
            return TypedResults.NotFound();
        }

        var streams = await db.SensorChunks.AsNoTracking()
            .Where(c => c.SessionId == id)
            .GroupBy(c => c.Kind)
            .Select(g => new { Kind = g.Key, Samples = g.Sum(c => c.SampleCount) })
            .ToDictionaryAsync(x => x.Kind, x => x.Samples, ct);

        var summary = s.SummaryJson is null ? null : JsonSerializer.Deserialize(s.SummaryJson, CsfJsonContext.Default.SessionSummary);
        var report = summary is null ? [] : SummaryReport.Build(summary, System.Globalization.CultureInfo.GetCultureInfo("it-IT"));

        audit.Record(http.User, "session.read", id);
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new SessionDetail(
            s.Id, s.PatientId, s.Patient!.Code,
            s.Patient.LastName is null ? null : $"{s.Patient.LastName} {s.Patient.FirstName}".Trim(),
            s.Mode, s.StartedAt, s.EndedAt, s.OperatorId, s.OperatorName, s.Notes, s.DeviceModel,
            Json(s.SummaryJson), report, streams,
            [.. s.Audio.OrderBy(a => a.CreatedAt).Select(a => new AudioItem(a.Id, a.CreatedAt, a.DurationSeconds, Json(a.FeaturesJson), storage.Resolve(a.StoragePath) is not null, a.SizeBytes))],
            [.. s.Analyses.OrderByDescending(a => a.CreatedAt).Select(a => new AnalysisItem(a.Id, a.CreatedAt, a.Model, a.Language, a.OperatorId, a.Response))]));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        string id, ClinicalDbContext db, AudioStorage storage, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
        {
            return TypedResults.NotFound();
        }

        db.Sessions.Remove(s);
        audit.Record(http.User, "session.delete", id);
        await db.SaveChangesAsync(ct);
        storage.DeleteSession(id);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<PoseReplay>> PoseAsync(string id, SessionAnalytics analytics, int maxFrames = 900, CancellationToken ct = default) =>
        TypedResults.Ok(await analytics.GetPoseReplayAsync(id, Math.Clamp(maxFrames, 10, 5000), ct));

    private static async Task<Ok<Spectrogram>> SpectrogramAsync(
        string id, SessionAnalytics analytics, int window = 256, int columns = 160, double maxHz = 20, CancellationToken ct = default)
    {
        window = window is 64 or 128 or 256 or 512 or 1024 ? window : 256;
        return TypedResults.Ok(await analytics.GetTremorSpectrogramAsync(id, window, Math.Clamp(columns, 10, 600), Math.Clamp(maxHz, 2, 50), ct));
    }

    private static async Task<Ok<StreamSeries>> StreamAsync(string id, SensorKind kind, SessionAnalytics analytics, int maxPoints = 2000, CancellationToken ct = default) =>
        TypedResults.Ok(await analytics.GetSeriesAsync(id, kind, Math.Clamp(maxPoints, 10, 20000), ct));

    /// <summary>Audio convertito al volo in WAV per la riproduzione nel browser.</summary>
    private static async Task<Results<EmptyHttpResult, NotFound>> AudioAsync(
        string id, string audioId, ClinicalDbContext db, AudioStorage storage, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var recording = await db.AudioRecordings.AsNoTracking().FirstOrDefaultAsync(a => a.Id == audioId && a.SessionId == id, ct);
        if (storage.Resolve(recording?.StoragePath) is not { } path)
        {
            return TypedResults.NotFound();
        }

        audit.Record(http.User, "audio.play", audioId);
        await db.SaveChangesAsync(ct);

        await using var caf = File.OpenRead(path);
        var info = CafToWav.Parse(caf);
        caf.Position = 0;
        http.Response.ContentType = "audio/wav";
        http.Response.ContentLength = CafToWav.WavLength(info);
        http.Response.Headers.CacheControl = "private, no-store";
        await CafToWav.WriteWavAsync(caf, http.Response.Body, ct);
        return TypedResults.Empty;
    }

    private static JsonElement? Json(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
}
