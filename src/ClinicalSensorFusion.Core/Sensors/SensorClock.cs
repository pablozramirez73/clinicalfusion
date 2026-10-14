using System.Diagnostics;

namespace ClinicalSensorFusion.Core.Sensors;

/// <summary>
/// Orologio monotono di sessione. Tutti i flussi usano la stessa base tempi per poter essere
/// allineati in analisi.
/// </summary>
/// <remarks>
/// Su iOS i timestamp di CoreMotion, ARKit e AVAudioEngine sono espressi in "system uptime"
/// (secondi dall'avvio). Il layer iOS crea l'orologio passando <c>NSProcessInfo.SystemUptime</c>
/// e converte i timestamp nativi con <see cref="FromUptime"/>: così il timestamp riflette l'istante
/// di acquisizione e non quello (ritardato e con jitter) in cui arriva la callback.
/// </remarks>
public sealed class SensorClock
{
    private readonly Func<double> _uptime;
    private readonly double _origin;

    public SensorClock(Func<double>? uptimeProvider = null)
    {
        _uptime = uptimeProvider ?? (static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _origin = _uptime();
    }

    /// <summary>Secondi trascorsi dall'inizio della sessione.</summary>
    public double Now => _uptime() - _origin;

    /// <summary>Converte un timestamp nativo (uptime) in secondi di sessione.</summary>
    public double FromUptime(double uptimeSeconds) => uptimeSeconds - _origin;
}
