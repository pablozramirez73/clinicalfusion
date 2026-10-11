using System.Globalization;
using System.Text.Json;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Endpoints;

public sealed record WeeklyActivity(DateOnly WeekStart, CaptureMode Mode, int Sessions);

public sealed record TremorPoint(string SessionId, string PatientCode, DateTimeOffset StartedAt, double FrequencyHz, double RmsMs2, double DurationSeconds);

public sealed record JointAverage(Joint Joint, string Name, double MeanRom, int Sessions);

public sealed record Overview(
    int Patients, int PlaceholderPatients, int Sessions, int SessionsLast30Days, double RecordedHours, int AudioFiles,
    IReadOnlyList<WeeklyActivity> Weekly, IReadOnlyList<JointAverage> JointAverages,
    IReadOnlyList<TremorPoint> Tremor, IReadOnlyList<SessionListItem> Recent);

public static class DashboardEndpoints
{
    public static RouteGroupBuilder MapDashboard(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard/overview", OverviewAsync).WithTags("Dashboard").RequireAuthorization(Policies.Clinician);
        return api;
    }

    private static async Task<Ok<Overview>> OverviewAsync(ClinicalDbContext db, TimeProvider clock, int weeks = 12, CancellationToken ct = default)
    {
        weeks = Math.Clamp(weeks, 4, 52);
        var now = clock.GetUtcNow();
        var since = now.AddDays(-7 * weeks);

        var patients = await db.Patients.CountAsync(ct);
        var placeholders = await db.Patients.CountAsync(p => p.IsPlaceholder, ct);
        var sessions = await db.Sessions.CountAsync(ct);
        var last30 = await db.Sessions.CountAsync(s => s.StartedAt >= now.AddDays(-30), ct);
        var seconds = await db.Sessions.SumAsync(s => s.DurationSeconds ?? 0, ct);
        var audio = await db.AudioRecordings.CountAsync(a => a.StoragePath != null, ct);

        var recentRows = await db.Sessions.AsNoTracking()
            .Where(s => s.StartedAt >= since)
            .Select(s => new { s.Id, s.StartedAt, s.Mode, s.SummaryJson, s.TremorFrequencyHz, s.TremorRmsMs2, s.DurationSeconds, Code = s.Patient!.Code })
            .ToListAsync(ct);

        // Raggruppamento per settimana ISO (lunedì) in memoria: poche centinaia di righe, query semplice e portabile.
        var weekly = recentRows
            .GroupBy(r => (Week: StartOfWeek(r.StartedAt), r.Mode))
            .Select(g => new WeeklyActivity(g.Key.Week, g.Key.Mode, g.Count()))
            .OrderBy(w => w.WeekStart).ThenBy(w => w.Mode)
            .ToList();

        var romByJoint = new Dictionary<Joint, (double Sum, int Count)>();
        foreach (var r in recentRows.Where(r => r.SummaryJson is not null))
        {
            var summary = JsonSerializer.Deserialize(r.SummaryJson!, CsfJsonContext.Default.SessionSummary);
            foreach (var (joint, range) in summary?.JointRanges ?? [])
            {
                var acc = romByJoint.GetValueOrDefault(joint);
                romByJoint[joint] = (acc.Sum + range.RangeOfMotion, acc.Count + 1);
            }
        }

        var jointAverages = romByJoint
            .OrderBy(j => j.Key)
            .Select(j => new JointAverage(j.Key, SummaryReport.JointName(j.Key), j.Value.Sum / j.Value.Count, j.Value.Count))
            .ToList();

        var tremor = recentRows
            .Where(r => r.TremorFrequencyHz is not null && r.TremorRmsMs2 is not null)
            .Select(r => new TremorPoint(r.Id, r.Code, r.StartedAt, r.TremorFrequencyHz!.Value, r.TremorRmsMs2!.Value, r.DurationSeconds ?? 0))
            .ToList();

        var recent = await db.Sessions.AsNoTracking().OrderByDescending(s => s.StartedAt).Take(8)
            .Select(s => new SessionListItem(
                s.Id, s.PatientId, s.Patient!.Code,
                s.Patient.LastName == null ? null : s.Patient.LastName + " " + s.Patient.FirstName,
                s.Mode, s.StartedAt, s.DurationSeconds, s.OperatorName, s.TremorFrequencyHz, s.LidarCoverage,
                s.Audio.Count, s.Analyses.Count))
            .ToListAsync(ct);

        return TypedResults.Ok(new Overview(patients, placeholders, sessions, last30, seconds / 3600, audio, weekly, jointAverages, tremor, recent));
    }

    private static DateOnly StartOfWeek(DateTimeOffset at)
    {
        var date = DateOnly.FromDateTime(at.UtcDateTime);
        var offset = ((int)date.DayOfWeek + 6) % 7; // lunedì = 0
        return date.AddDays(-offset);
    }
}
