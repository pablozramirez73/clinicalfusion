using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Bluetooth;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Sensori Bluetooth Low Energy con profili standard (frequenza cardiaca, saturimetro, termometro): ricerca,
/// collegamento e misure in arrivo. Le misure portano un timestamp provvisorio (0): chi registra le data
/// con l'orologio della rilevazione.
/// </summary>
public interface IBluetoothSensors
{
    /// <summary>Falso dove il Bluetooth non è (ancora) gestito dall'app.</summary>
    bool IsSupported { get; }

    bool IsScanning { get; }

    /// <summary>Sensori collegati in questo momento.</summary>
    IReadOnlyList<BleDevice> Connected { get; }

    /// <summary>Sensore trovato o aggiornato (segnale) durante la ricerca.</summary>
    event EventHandler<BleDevice>? DeviceFound;

    /// <summary>Un sensore si è collegato o scollegato.</summary>
    event EventHandler? ConnectionChanged;

    event EventHandler<SensorReading>? Measurement;

    /// <summary>Avvia la ricerca. Restituisce un messaggio per l'operatore se non si può (Bluetooth spento, permesso negato).</summary>
    Task<string?> StartScanAsync();

    void StopScan();

    /// <summary>Collega un sensore trovato dalla ricerca e lo ricorda per i prossimi avvii.</summary>
    void Connect(string deviceId);

    /// <summary>Scollega e dimentica il sensore.</summary>
    void Disconnect(string deviceId);

    /// <summary>Ricollega i sensori ricordati, se sono accesi e nelle vicinanze. Non chiede permessi se non ce ne sono.</summary>
    void ReconnectRemembered();
}

/// <summary>Piattaforme senza gestione del Bluetooth: nessun sensore, nessun errore.</summary>
internal sealed class NullBluetoothSensors : IBluetoothSensors
{
    public bool IsSupported => false;

    public bool IsScanning => false;

    public IReadOnlyList<BleDevice> Connected => [];

#pragma warning disable CS0067 // Mai sollevati: qui non c'è nessun sensore.
    public event EventHandler<BleDevice>? DeviceFound;

    public event EventHandler? ConnectionChanged;

    public event EventHandler<SensorReading>? Measurement;
#pragma warning restore CS0067

    public Task<string?> StartScanAsync() => Task.FromResult<string?>(Loc.T("I sensori Bluetooth non sono ancora gestiti su questo dispositivo."));

    public void StopScan()
    {
    }

    public void Connect(string deviceId)
    {
    }

    public void Disconnect(string deviceId)
    {
    }

    public void ReconnectRemembered()
    {
    }
}

/// <summary>Sensori ricordati tra un avvio e l'altro (solo identificativo, nome e tipo: nessuna misura).</summary>
internal sealed class RememberedBleDevices(IPreferences preferences)
{
    private const string Key = "bluetooth.devices";

    public IReadOnlyList<BleDevice> Load()
    {
        var result = new List<BleDevice>();
        foreach (var line in preferences.Get(Key, string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length == 3 && Enum.TryParse<BleSensorType>(parts[1], out var type))
            {
                result.Add(new BleDevice(parts[0], parts[2], 0, type));
            }
        }

        return result;
    }

    public void Add(BleDevice device) => Save([.. Load().Where(d => d.Id != device.Id), device]);

    public void Remove(string deviceId) => Save([.. Load().Where(d => d.Id != deviceId)]);

    private void Save(IEnumerable<BleDevice> devices) => preferences.Set(
        Key,
        string.Join('\n', devices.Select(d => $"{d.Id}\t{d.Type}\t{d.Name.Replace('\t', ' ').Replace('\n', ' ')}")));
}

/// <summary>Porta nella rilevazione in corso le misure dei sensori Bluetooth collegati.</summary>
internal sealed class BluetoothSensorSource(IBluetoothSensors bluetooth, SensorClock clock) : ISensorSource
{
    private ISensorSink? _sink;

    public string DisplayName => Loc.T("Sensori Bluetooth");

    public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.HeartRate, SensorKind.PulseOximeter, SensorKind.BodyTemperature];

    public bool IsAvailable => bluetooth.Connected.Count > 0;

    public Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sink = sink;
        bluetooth.Measurement += OnMeasurement;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        bluetooth.Measurement -= OnMeasurement;
        _sink = null;
        return Task.CompletedTask;
    }

    public void Dispose() => bluetooth.Measurement -= OnMeasurement;

    // Il sensore non ha un orologio in comune con il telefono: vale l'istante di arrivo della notifica.
    private void OnMeasurement(object? sender, SensorReading reading) =>
        _sink?.TryWrite(new SensorReading(reading.Kind, clock.Now, reading.Values));
}
