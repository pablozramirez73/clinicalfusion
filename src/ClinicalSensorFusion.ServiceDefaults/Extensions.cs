using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Servizi comuni a ogni progetto orchestrato da Aspire. Il servizio aggiunge sopra le proprie sorgenti di
/// telemetria e i propri controlli di salute: qui c'è solo ciò che vale per tutti.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Sonda di prontezza: esegue i controlli delle dipendenze.</summary>
    public const string HealthPath = "/health";

    /// <summary>Sonda di liveness: il processo risponde, nessun controllo eseguito.</summary>
    public const string AlivePath = "/health/live";

    public const string LiveTag = "live";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });
        return builder;
    }

    /// <summary>
    /// Log, metriche e tracce di ASP.NET Core, dei client HTTP e del runtime, esportati via OTLP se è impostato
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (Aspire lo imposta verso la propria dashboard).
    /// </summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(t => t
                .AddSource(builder.Environment.ApplicationName)
                // Le sonde degli orchestratori arrivano ogni pochi secondi: tracciarle è solo rumore.
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                    !ctx.Request.Path.StartsWithSegments(HealthPath) && !ctx.Request.Path.StartsWithSegments(AlivePath))
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>Controllo «self» sempre sano, per la sonda di liveness.</summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);
        return builder;
    }

    /// <summary>
    /// Sonde <see cref="HealthPath"/> e <see cref="AlivePath"/>, solo in sviluppo e solo se il servizio non le ha
    /// già mappate per conto suo (il server ha le proprie, con limiti di frequenza e risposta ridotta): così le
    /// rotte non si duplicano e l'AppHost le usa in entrambi i casi.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        var mapped = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText?.TrimStart('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!mapped.Contains(HealthPath))
        {
            app.MapHealthChecks(HealthPath);
        }

        if (!mapped.Contains(AlivePath))
        {
            app.MapHealthChecks(AlivePath, new HealthCheckOptions { Predicate = r => r.Tags.Contains(LiveTag) });
        }

        return app;
    }
}
