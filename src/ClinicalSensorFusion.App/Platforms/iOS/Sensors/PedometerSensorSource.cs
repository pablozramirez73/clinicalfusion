using ClinicalSensorFusion.Core.Sensors;
using CoreMotion;
using Foundation;

namespace ClinicalSensorFusion.App.Platforms.iOS.Sensors;

/// <summary>
/// Pedometro (coprocessore di movimento): passi, distanza, cadenza, piani. I valori sono cumulativi
/// dall'inizio della rilevazione e arrivano con qualche secondo di ritardo: va bene per un riepilogo
/// (es. test del cammino di 6 minuti), non per l'analisi passo-passo (per quella c'è l'IMU a 100 Hz).
/// </summary>
internal sealed class PedometerSensorSource(SensorClock clock) : ISensorSource
{
    private readonly CMPedometer _pedometer = new();

    public string DisplayName => "Pedometro";

    public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Pedometer];

    public bool IsAvailable => CMPedometer.IsStepCountingAvailable;

    public Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        if (IsAvailable)
        {
            _pedometer.StartPedometerUpdates(NSDate.Now, (data, error) =>
            {
                if (data is null)
                {
                    return;
                }

                sink.TryWrite(new SensorReading(SensorKind.Pedometer, clock.Now,
                [
                    data.NumberOfSteps.FloatValue,
                    data.Distance?.FloatValue ?? float.NaN,
                    data.CurrentCadence?.FloatValue ?? float.NaN,
                    data.FloorsAscended?.FloatValue ?? 0,
                    data.FloorsDescended?.FloatValue ?? 0,
                ]));
            });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _pedometer.StopPedometerUpdates();
        return Task.CompletedTask;
    }

    public void Dispose() => _pedometer.Dispose();
}
