using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Telemetry;

/// <summary>
/// Un'operazione con inizio, fine ed esito (invio di una rilevazione, richiesta al modello). Alla chiusura
/// registra la durata nell'istogramma con l'attributo <c>outcome</c>, chiude la traccia e scrive una riga di
/// log: informazione se riuscita, avviso altrimenti.
/// </summary>
public sealed partial class TelemetryOperation : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly Histogram<double> _duration;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Activity? _activity;
    private TagList _tags;
    private string _outcome = CsfTelemetry.Outcomes.Error;
    private Exception? _error;
    private bool _disposed;

    internal TelemetryOperation(ILogger logger, string name, Histogram<double> duration)
    {
        _logger = logger;
        _name = name;
        _duration = duration;
        _activity = CsfTelemetry.Activities.StartActivity(name);
    }

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    /// <summary>Attributo a bassa cardinalità, riportato sia sulla metrica sia sulla traccia.</summary>
    public TelemetryOperation Tag(string key, object? value)
    {
        _tags.Add(key, value);
        _activity?.SetTag(key, value);
        return this;
    }

    public void Succeed() => _outcome = CsfTelemetry.Outcomes.Success;

    /// <summary>Classifica l'errore: annullamento, timeout o errore generico.</summary>
    public void Fail(Exception error, bool cancelledByCaller = false)
    {
        _error = error;
        _outcome = error switch
        {
            OperationCanceledException when cancelledByCaller => CsfTelemetry.Outcomes.Cancelled,
            OperationCanceledException or TimeoutException => CsfTelemetry.Outcomes.Timeout,
            _ when error.InnerException is OperationCanceledException or TimeoutException => CsfTelemetry.Outcomes.Timeout,
            _ => CsfTelemetry.Outcomes.Error,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var seconds = Elapsed.TotalSeconds;
        _tags.Add("outcome", _outcome);
        _duration.Record(seconds, _tags);

        if (_activity is not null)
        {
            _activity.SetTag("outcome", _outcome);
            _activity.SetStatus(_outcome == CsfTelemetry.Outcomes.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, _error?.Message);
            _activity.Dispose();
        }

        if (_outcome == CsfTelemetry.Outcomes.Success)
        {
            LogCompleted(_logger, _name, seconds);
        }
        else
        {
            LogFailed(_logger, _name, _outcome, seconds, _error?.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Operazione {Operation} completata in {DurationSeconds:F3} s.")]
    private static partial void LogCompleted(ILogger logger, string operation, double durationSeconds);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "Operazione {Operation} terminata con esito {Outcome} dopo {DurationSeconds:F3} s ({ErrorType}).")]
    private static partial void LogFailed(ILogger logger, string operation, string outcome, double durationSeconds, string? errorType);
}
