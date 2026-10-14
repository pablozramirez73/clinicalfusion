using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Sensors;
using Foundation;
using UIKit;

namespace ClinicalSensorFusion.App.Platforms.iOS.Sensors;

/// <summary>
/// Sensore di prossimità e stato del dispositivo (stato termico, batteria).
/// </summary>
/// <remarks>
/// Prossimità: utile per sapere se il telefono è appoggiato al corpo (es. fissato all'arto per il test del
/// tremore). Effetto collaterale di iOS: con un oggetto vicino lo schermo si spegne.
/// Stato termico: con LiDAR + camera + inferenza GPU l'iPhone si scalda; da "serious" in su iOS riduce le
/// prestazioni e la frequenza dei fotogrammi cala → lo si registra come indicatore di qualità.
/// </remarks>
internal sealed class SystemSensorSource(SensorClock clock) : ISensorSource
{
    private NSObject? _proximityObserver;
    private Timer? _timer;
    private ISensorSink? _sink;

    public string DisplayName => Loc.T("Prossimità · stato termico · batteria");

    public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Proximity, SensorKind.DeviceState];

    public bool IsAvailable => true;

    public Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sink = sink;
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            var device = UIDevice.CurrentDevice;
            device.BatteryMonitoringEnabled = true;
            device.ProximityMonitoringEnabled = true;
            if (device.ProximityMonitoringEnabled)
            {
                _proximityObserver = UIDevice.Notifications.ObserveProximityStateDidChange((_, _) =>
                    _sink?.TryWrite(new SensorReading(SensorKind.Proximity, clock.Now, [UIDevice.CurrentDevice.ProximityState ? 1f : 0f])));
            }

            // UIDevice va letto sul main thread (UIKit).
            _timer = new Timer(_ => MainThread.BeginInvokeOnMainThread(WriteDeviceState), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        });
    }

    public Task StopAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        _timer?.Dispose();
        _timer = null;
        _proximityObserver?.Dispose();
        _proximityObserver = null;
        UIDevice.CurrentDevice.ProximityMonitoringEnabled = false;
        UIDevice.CurrentDevice.BatteryMonitoringEnabled = false;
    });

    public void Dispose()
    {
        _timer?.Dispose();
        _proximityObserver?.Dispose();
    }

    private void WriteDeviceState()
    {
        var thermal = (float)NSProcessInfo.ProcessInfo.ThermalState;
        var battery = UIDevice.CurrentDevice.BatteryLevel;
        _sink?.TryWrite(new SensorReading(SensorKind.DeviceState, clock.Now, [thermal, battery]));
    }
}
