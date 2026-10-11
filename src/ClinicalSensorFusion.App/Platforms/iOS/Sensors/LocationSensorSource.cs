using ClinicalSensorFusion.Core.Sensors;
using CoreLocation;

namespace ClinicalSensorFusion.App.Platforms.iOS.Sensors;

/// <summary>GPS (CoreLocation). Attivo solo se abilitato nelle impostazioni: è un dato personale non necessario alla clinica.</summary>
internal sealed class LocationSensorSource(SensorClock clock) : ISensorSource
{
    private CLLocationManager? _manager;
    private ISensorSink? _sink;

    public string DisplayName => "Posizione (GPS)";

    public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Location];

    public bool IsAvailable => CLLocationManager.LocationServicesEnabled;

    public Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sink = sink;
        // CLLocationManager va creato su un thread con run loop (il main thread).
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            _manager = new CLLocationManager
            {
                DesiredAccuracy = CLLocation.AccuracyBest,
                DistanceFilter = 1,
                ActivityType = CLActivityType.Fitness,
            };
            _manager.LocationsUpdated += OnLocations;
            _manager.RequestWhenInUseAuthorization();
            _manager.StartUpdatingLocation();
        });
    }

    public Task StopAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (_manager is null)
        {
            return;
        }

        _manager.StopUpdatingLocation();
        _manager.LocationsUpdated -= OnLocations;
        _manager.Dispose();
        _manager = null;
    });

    public void Dispose() => _manager?.Dispose();

    private void OnLocations(object? sender, CLLocationsUpdatedEventArgs e)
    {
        foreach (var l in e.Locations)
        {
            _sink?.TryWrite(new SensorReading(SensorKind.Location, clock.Now,
            [
                (float)l.Coordinate.Latitude, (float)l.Coordinate.Longitude, (float)l.Altitude,
                (float)l.HorizontalAccuracy, (float)l.Speed,
            ]));
        }
    }
}
