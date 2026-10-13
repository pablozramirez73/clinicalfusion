using System.Diagnostics;
using ClinicalSensorFusion.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Diagnostica dell'app: log su file (nel contenitore dell'app, mai inviati da soli), metriche in memoria
/// riversate nel log quando l'app va in background, eccezioni non gestite. Niente servizi di terze parti:
/// i file escono dal telefono solo se l'operatore li condivide dalle Impostazioni.
/// </summary>
public sealed class AppDiagnostics(RollingFileLoggerProvider files, MetricsCollector metrics, ILogger<AppDiagnostics> logger)
{
    /// <summary>Avviato all'ingresso di <c>CreateMauiApp</c>: misura l'avvio fino alla prima finestra.</summary>
    public static readonly Stopwatch SinceLaunch = Stopwatch.StartNew();

    private int _hooked;
    private int _started;

    public static string LogDirectory => Path.Combine(AppPaths.Library, "logs");

    public static RollingFileLoggerOptions FileOptions => new() { Directory = LogDirectory, FilePrefix = "app" };

    /// <summary>Registra le eccezioni che altrimenti sparirebbero con il processo.</summary>
    public void HookUnhandledExceptions()
    {
        if (Interlocked.Exchange(ref _hooked, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            CsfTelemetry.App.UnhandledExceptions.Add(1, CsfTelemetry.Tag("source", "appdomain"));
            logger.LogCritical(e.ExceptionObject as Exception, "Eccezione non gestita: l'app sta per chiudersi.");
            // Il processo sta morendo: l'unica occasione per portare la riga su disco.
            files.FlushAsync().Wait(TimeSpan.FromSeconds(2));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CsfTelemetry.App.UnhandledExceptions.Add(1, CsfTelemetry.Tag("source", "task"));
            logger.LogError(e.Exception, "Eccezione non osservata in un'operazione in background.");
            e.SetObserved();
        };
    }

    public void Attach(Window window)
    {
        window.Created += (_, _) =>
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            var seconds = SinceLaunch.Elapsed.TotalSeconds;
            CsfTelemetry.App.StartDuration.Record(seconds);
            logger.LogInformation(
                "App avviata in {StartSeconds:F2} s: versione {Version} (build {Build}), {Platform} {OsVersion}, {DeviceModel}.",
                seconds, AppInfo.Current.VersionString, AppInfo.Current.BuildString,
                DeviceInfo.Current.Platform, DeviceInfo.Current.VersionString, DeviceInfo.Current.Model);
        };

        // In background il sistema può chiudere l'app senza preavviso: si salva lo stato delle metriche.
        window.Stopped += (_, _) => _ = SnapshotAsync();
    }

    /// <summary>Scrive nel log i totali delle metriche e porta tutto su disco.</summary>
    public async Task SnapshotAsync()
    {
        logger.LogInformation("Metriche: {Metrics}", MetricsCollector.Describe(metrics.Snapshot()));
        await files.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>File di log pronti per la condivisione, dal più recente.</summary>
    public async Task<IReadOnlyList<string>> ExportAsync()
    {
        await SnapshotAsync().ConfigureAwait(false);
        return files.Files;
    }
}
