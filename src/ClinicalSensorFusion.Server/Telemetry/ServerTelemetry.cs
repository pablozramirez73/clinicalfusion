using System.Diagnostics;
using System.Reflection;
using ClinicalSensorFusion.Core.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ClinicalSensorFusion.Server.Telemetry;

/// <summary>Sezione <c>Telemetry</c>.</summary>
public sealed class TelemetryOptions
{
    /// <summary>
    /// Raccoglitore OTLP (gRPC, es. <c>http://otel-collector:4317</c>) per metriche, tracce e log. Vuoto = nessuna
    /// esportazione: i dati restano nel processo (pagina di stato) e nei log. Vale anche la variabile standard
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.
    /// </summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>Cartella per i log su file a rotazione (JSON, una riga per evento). Vuoto = solo console.</summary>
    public string? LogDirectory { get; init; }

    public string ServiceName { get; init; } = "clinical-sensor-fusion-server";
}

public static class ServerTelemetry
{
    /// <summary>Meter di ASP.NET Core con latenza, traffico ed errori delle richieste (<c>http.server.request.duration</c>).</summary>
    public const string HttpMeter = "Microsoft.AspNetCore.Hosting";

    public static string Version { get; } =
        typeof(ServerTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ServerTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Log strutturati (JSON su console fuori dallo sviluppo, file a rotazione se configurato), metriche e
    /// tracce con OpenTelemetry, aggregatore in memoria per la pagina di stato.
    /// </summary>
    public static WebApplicationBuilder AddCsfTelemetry(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection("Telemetry"));

        if (!builder.Environment.IsDevelopment())
        {
            // Una riga JSON per evento, con gli scope (traccia, utente): leggibile da qualunque raccoglitore di log.
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "O";
            });
        }

        if (!string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            builder.Logging.AddProvider(new RollingFileLoggerProvider(new RollingFileLoggerOptions
            {
                Directory = options.LogDirectory,
                FilePrefix = "server",
                MaxFileBytes = 10 * 1024 * 1024,
                MaxFiles = 10,
            }));
        }

        // Creato subito: deve essere in ascolto prima della prima richiesta.
        builder.Services.AddSingleton(new MetricsCollector(HttpMeter));

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(options.ServiceName, serviceVersion: Version))
            .WithMetrics(m => m
                .AddMeter(CsfTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(t => t
                .AddSource(CsfTelemetry.Name)
                .AddAspNetCoreInstrumentation());

        var endpoint = options.OtlpEndpoint ?? builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            otel.WithLogging(null, o =>
            {
                o.IncludeScopes = true;
                o.IncludeFormattedMessage = true;
            });
            otel.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(endpoint));
        }

        return builder;
    }

    /// <summary>
    /// Una riga di log per ogni richiesta alle API, con metodo, rotta (il modello, non l'URL con gli
    /// identificativi), stato e durata; identificativo di traccia negli scope e nell'intestazione
    /// <c>X-Trace-Id</c>, così una segnalazione dell'utente si ritrova nei log.
    /// </summary>
    public static IApplicationBuilder UseCsfRequestLogging(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("ClinicalSensorFusion.Server.Requests");
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            context.Response.Headers["X-Trace-Id"] = traceId;
            var started = Stopwatch.GetTimestamp();
            using var scope = logger.BeginScope(new Dictionary<string, object?> { ["TraceId"] = traceId });
            var failed = false;
            try
            {
                await next();
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(nessuna rotta)";
                var level = status >= 500 ? LogLevel.Error : status is 401 or 403 or 429 ? LogLevel.Warning : LogLevel.Information;
                logger.Log(
                    level,
                    "HTTP {Method} {Route} → {StatusCode} in {DurationMs:F1} ms (utente: {User})",
                    context.Request.Method, route, status, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    context.User.Identity?.Name ?? "anonimo");
            }
        });
    }
}
