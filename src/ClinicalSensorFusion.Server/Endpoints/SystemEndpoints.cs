using System.Diagnostics;
using System.Text.Json;
using ClinicalSensorFusion.Core.Telemetry;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClinicalSensorFusion.Server.Endpoints;

public sealed record HealthCheckItem(string Name, string Status, string? Description, double DurationMs, IReadOnlyDictionary<string, object> Data);

public sealed record SystemHealth(
    string Status, string Version, string Environment, DateTimeOffset StartedAt, double UptimeSeconds,
    double WorkingSetMb, double GcHeapMb, int ThreadCount, IReadOnlyList<HealthCheckItem> Checks);

public sealed record SystemMetrics(DateTimeOffset Since, IReadOnlyList<MetricPoint> Points);

/// <summary>
/// Stato del servizio. Tre livelli: <c>/health/live</c> (il processo risponde), <c>/health</c> e
/// <c>/health/ready</c> (dipendenze raggiungibili; anonimi, solo lo stato di ogni controllo) e
/// <c>/api/v1/system/*</c> (dettagli e metriche, solo per i clinici: è ciò che mostra la pagina «Stato del sistema»).
/// </summary>
public static class SystemEndpoints
{
    public const string ReadyTag = "ready";

    /// <summary>Nome della policy dei limiti di frequenza per le sonde anonime che eseguono i controlli.</summary>
    public const string HealthRateLimit = "health";

    private static readonly DateTimeOffset Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static WebApplication MapHealth(this WebApplication app)
    {
        // La sonda di liveness risponde senza eseguire alcun controllo: è quella degli orchestratori, resta
        // fuori dai limiti di frequenza. Mai in cache.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync })
            .AllowAnonymous().DisableRateLimiting();

        // Queste due eseguono i controlli di database e archivio audio: anonime ma limitate per indirizzo IP,
        // altrimenti un flood anonimo diventa amplificazione (connessioni al database e scritture su disco).
        foreach (var path in (string[])["/health", "/health/ready"])
        {
            app.MapHealthChecks(path, new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync })
                .AllowAnonymous().RequireRateLimiting(HealthRateLimit);
        }

        return app;
    }

    public static RouteGroupBuilder MapSystem(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/system").WithTags("Stato del sistema").RequireAuthorization(Policies.Clinician);
        g.MapGet("/health", HealthAsync);
        g.MapGet("/metrics", Metrics);
        return api;
    }

    private static async Task<Ok<SystemHealth>> HealthAsync(HealthCheckService health, IHostEnvironment environment, CancellationToken ct)
    {
        var report = await health.CheckHealthAsync(ct);
        using var process = Process.GetCurrentProcess();
        return TypedResults.Ok(new SystemHealth(
            report.Status.ToString(),
            ServerTelemetry.Version,
            environment.EnvironmentName,
            Started,
            (DateTimeOffset.UtcNow - Started).TotalSeconds,
            process.WorkingSet64 / 1048576.0,
            GC.GetTotalMemory(forceFullCollection: false) / 1048576.0,
            process.Threads.Count,
            [.. report.Entries.Select(e => new HealthCheckItem(
                e.Key, e.Value.Status.ToString(), e.Value.Description, e.Value.Duration.TotalMilliseconds,
                e.Value.Data.ToDictionary(d => d.Key, d => d.Value)))]));
    }

    private static Ok<SystemMetrics> Metrics(MetricsCollector collector) =>
        TypedResults.Ok(new SystemMetrics(Started, collector.Snapshot()));

    /// <summary>Solo nomi e stati: niente descrizioni né eccezioni a chi non è autenticato.</summary>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        }));
    }
}
