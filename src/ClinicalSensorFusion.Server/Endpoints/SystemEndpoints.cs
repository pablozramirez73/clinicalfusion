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

    private static readonly DateTimeOffset Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static WebApplication MapHealth(this WebApplication app)
    {
        // Sonde per orchestratori e monitoraggio: fuori dai limiti di frequenza, mai in cache.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync })
            .AllowAnonymous().DisableRateLimiting();
        // Le sonde di prontezza toccano database e disco e sono anonime: il risultato si riusa per qualche
        // secondo, così un'ondata di richieste costa un solo controllo da qualunque indirizzo arrivi. Restano
        // fuori dai limiti di frequenza: il monitoraggio deve vedere lo stato anche quando un indirizzo li supera.
        foreach (var path in (string[])["/health", "/health/ready"])
        {
            app.MapGet(path, async (HttpContext http, ReadinessCache cache, CancellationToken ct) =>
            {
                var report = await cache.GetAsync(ct);
                http.Response.StatusCode = report.Status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK;
                await WriteAsync(http, report);
            }).AllowAnonymous().DisableRateLimiting().ExcludeFromDescription();
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

    /// <summary>
    /// Ultimo esito dei controlli di prontezza, rifatto al più ogni <see cref="MaxAge"/>. Richieste concorrenti
    /// attendono lo stesso controllo invece di avviarne uno ciascuna.
    /// </summary>
    public sealed class ReadinessCache(HealthCheckService health, TimeProvider clock)
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private HealthReport? _report;
        private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;

        public async Task<HealthReport> GetAsync(CancellationToken ct)
        {
            if (_report is { } fresh && clock.GetUtcNow() - _checkedAt < MaxAge)
            {
                return fresh;
            }

            await _gate.WaitAsync(ct);
            try
            {
                if (_report is null || clock.GetUtcNow() - _checkedAt >= MaxAge)
                {
                    // Senza il token della richiesta: un client che chiude la connessione non annulla il controllo degli altri.
                    _report = await health.CheckHealthAsync(c => c.Tags.Contains(ReadyTag), CancellationToken.None);
                    _checkedAt = clock.GetUtcNow();
                }

                return _report;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

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
