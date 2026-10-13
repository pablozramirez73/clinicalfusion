using ClinicalSensorFusion.Core.Localization;
using Android.Content;
using Android.Hardware;
using Android.OS;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.App.Platforms.Android.Sensors;

/// <summary>Barometro: quota relativa all'inizio della rilevazione e pressione. Utile per scale e alzate dalla sedia.</summary>
internal sealed class BarometerSensorSource(SensorClock clock) : AndroidSensorSource(clock)
{
    private float _initialPressure = float.NaN;

    public override string DisplayName => Loc.T("Barometro");

    public override IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Barometer];

    protected override IReadOnlyList<SensorType> Types { get; } = [SensorType.Pressure];

    protected override int SamplingPeriodUs => 1_000_000;

    protected override void OnReading(Sensor sensor, IList<float> values, double time)
    {
        // Android dà la pressione in hPa; l'archivio (come CMAltimeter) la vuole in kPa.
        var pressure = values[0];
        if (float.IsNaN(_initialPressure))
        {
            _initialPressure = pressure;
        }

        Sink?.TryWrite(new SensorReading(SensorKind.Barometer, time, [SensorManager.GetAltitude(_initialPressure, pressure), pressure / 10f]));
    }
}

/// <summary>
/// Contapassi di sistema. Il valore è cumulativo dall'avvio del telefono: si sottrae quello della prima
/// lettura. A differenza del pedometro iOS non stima distanza, cadenza e piani (restano NaN / 0); per
/// l'analisi passo-passo c'è l'IMU a 100 Hz.
/// </summary>
internal sealed class PedometerSensorSource(SensorClock clock) : AndroidSensorSource(clock)
{
    private float _initialSteps = float.NaN;

    public override string DisplayName => Loc.T("Pedometro");

    public override IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Pedometer];

    protected override IReadOnlyList<SensorType> Types { get; } = [SensorType.StepCounter];

    protected override void OnReading(Sensor sensor, IList<float> values, double time)
    {
        if (float.IsNaN(_initialSteps))
        {
            _initialSteps = values[0];
        }

        // L'evento porta l'istante dell'ultimo passo, anche precedente alla rilevazione: si usa l'ora di arrivo.
        Sink?.TryWrite(new SensorReading(SensorKind.Pedometer, Clock.Now, [values[0] - _initialSteps, float.NaN, float.NaN, 0, 0]));
    }
}

/// <summary>
/// Sensore di luce ambientale. Su Android è una misura vera in lux (su iOS è una stima di ARKit in lumen):
/// stesso flusso, ma i valori dei due sistemi non sono confrontabili tra loro. La temperatura di colore non c'è.
/// </summary>
internal sealed class LightSensorSource(SensorClock clock) : AndroidSensorSource(clock)
{
    private double _last = double.NegativeInfinity;

    public override string DisplayName => Loc.T("Luce ambientale");

    public override IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.AmbientLight];

    protected override IReadOnlyList<SensorType> Types { get; } = [SensorType.Light];

    protected override void OnReading(Sensor sensor, IList<float> values, double time)
    {
        // 2 Hz bastano (varia lentamente), evita di riempire il DB.
        if (time - _last >= 0.5)
        {
            _last = time;
            Sink?.TryWrite(new SensorReading(SensorKind.AmbientLight, time, [values[0], float.NaN]));
        }
    }
}

/// <summary>
/// Sensore di prossimità e stato del dispositivo (stato termico, batteria).
/// </summary>
/// <remarks>
/// Prossimità: utile per sapere se il telefono è appoggiato al corpo (es. fissato all'arto per il test del
/// tremore). Stato termico: camera + inferenza scaldano il telefono e il sistema riduce le prestazioni; lo
/// si registra come indicatore di qualità, riportato alla scala iOS (0 nominale … 3 critico).
/// </remarks>
internal sealed class SystemSensorSource(SensorClock clock) : AndroidSensorSource(clock)
{
    private Timer? _timer;

    public override string DisplayName => Loc.T("Prossimità · stato termico · batteria");

    public override IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Proximity, SensorKind.DeviceState];

    public override bool IsAvailable => true;

    protected override IReadOnlyList<SensorType> Types { get; } = [SensorType.Proximity];

    public override async Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        await base.StartAsync(sink, cancellationToken);
        _timer = new Timer(_ => WriteDeviceState(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public override Task StopAsync()
    {
        _timer?.Dispose();
        _timer = null;
        return base.StopAsync();
    }

    protected override void OnReading(Sensor sensor, IList<float> values, double time) =>
        // Molti sensori sono binari (0 oppure la portata massima): "vicino" è tutto ciò che sta sotto la portata.
        Sink?.TryWrite(new SensorReading(SensorKind.Proximity, time, [values[0] < sensor.MaximumRange ? 1f : 0f]));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void WriteDeviceState()
    {
        var context = global::Android.App.Application.Context;
        var thermal = (context.GetSystemService(Context.PowerService) as PowerManager)?.CurrentThermalStatus switch
        {
            null or ThermalStatus.None => 0f,
            ThermalStatus.Light or ThermalStatus.Moderate => 1f,
            ThermalStatus.Severe => 2f,
            _ => 3f,
        };
        var battery = (context.GetSystemService(Context.BatteryService) as BatteryManager)?.GetIntProperty((int)BatteryProperty.Capacity) is { } percent and >= 0
            ? percent / 100f
            : float.NaN;
        Sink?.TryWrite(new SensorReading(SensorKind.DeviceState, Clock.Now, [thermal, battery]));
    }
}

/// <summary>GPS. Attivo solo se abilitato nelle impostazioni: è un dato personale non necessario alla clinica.</summary>
internal sealed class LocationSensorSource(SensorClock clock) : ISensorSource
{
    private ISensorSink? _sink;

    public string DisplayName => Loc.T("Posizione (GPS)");

    public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Location];

    public bool IsAvailable => true;

    public async Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        // Senza permesso la rilevazione prosegue comunque, senza posizione.
        if (await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
        {
            return;
        }

        _sink = sink;
        Geolocation.Default.LocationChanged += OnLocation;
        await Geolocation.Default.StartListeningForegroundAsync(new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(1)));
    }

    public Task StopAsync()
    {
        Geolocation.Default.LocationChanged -= OnLocation;
        Geolocation.Default.StopListeningForeground();
        _sink = null;
        return Task.CompletedTask;
    }

    public void Dispose() => Geolocation.Default.LocationChanged -= OnLocation;

    private void OnLocation(object? sender, GeolocationLocationChangedEventArgs e)
    {
        var l = e.Location;
        _sink?.TryWrite(new SensorReading(SensorKind.Location, clock.Now,
        [
            (float)l.Latitude, (float)l.Longitude, (float)(l.Altitude ?? double.NaN),
            (float)(l.Accuracy ?? double.NaN), (float)(l.Speed ?? double.NaN),
        ]));
    }
}
