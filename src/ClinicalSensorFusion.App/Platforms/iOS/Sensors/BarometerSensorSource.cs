using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Sensors;
using CoreMotion;
using Foundation;

namespace ClinicalSensorFusion.App.Platforms.iOS.Sensors;

/// <summary>Barometro (CMAltimeter): quota relativa e pressione. Utile per scale e alzate dalla sedia.</summary>
internal sealed class BarometerSensorSource(SensorClock clock) : ISensorSource
{
    private readonly CMAltimeter _altimeter = new();
    private readonly NSOperationQueue _queue = new() { Name = "csf.altimeter", MaxConcurrentOperationCount = 1 };

    public string DisplayName => Loc.T("Barometro");

    public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Barometer];

    public bool IsAvailable => CMAltimeter.IsRelativeAltitudeAvailable;

    public Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        if (IsAvailable)
        {
            _altimeter.StartRelativeAltitudeUpdates(_queue, (data, error) =>
            {
                if (data is null)
                {
                    return;
                }

                sink.TryWrite(new SensorReading(SensorKind.Barometer, clock.FromUptime(data.Timestamp),
                    [data.RelativeAltitude.FloatValue, data.Pressure.FloatValue]));
            });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _altimeter.StopRelativeAltitudeUpdates();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _altimeter.Dispose();
        _queue.Dispose();
    }
}
