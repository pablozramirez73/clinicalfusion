using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Bluetooth;
using ClinicalSensorFusion.Core.Sensors;
using CoreBluetooth;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Platforms.iOS.Bluetooth;

/// <summary>
/// Sensori Bluetooth con CoreBluetooth. La ricerca è limitata ai servizi sanitari supportati: non elenca
/// gli altri dispositivi nei dintorni. Il gestore si crea solo quando serve, perché è la sua creazione a far
/// comparire la richiesta del permesso Bluetooth.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Singleton per la vita dell'app: gestore e coda Bluetooth non si rilasciano mai.")]
internal sealed class IosBluetoothSensors(RememberedBleDevices remembered, ILogger<IosBluetoothSensors> logger) : IBluetoothSensors
{
    private readonly DispatchQueue _queue = new("csf.bluetooth");
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CBPeripheral> _peripherals = [];
    private readonly Dictionary<string, BleDevice> _known = [];
    private readonly Dictionary<string, BleDevice> _connected = [];
    private CBCentralManager? _central;
    private CentralDelegate? _centralDelegate;
    private PeripheralDelegate? _peripheralDelegate;
    private TaskCompletionSource<CBManagerState>? _poweredOn;
    private bool _wantScan;
    private bool _wantReconnect;

    public bool IsSupported => true;

    public bool IsScanning { get; private set; }

    public IReadOnlyList<BleDevice> Connected
    {
        get
        {
            lock (_gate)
            {
                return [.. _connected.Values];
            }
        }
    }

    public event EventHandler<BleDevice>? DeviceFound;

    public event EventHandler? ConnectionChanged;

    public event EventHandler<SensorReading>? Measurement;

    private static CBUUID[] ServiceIds => [.. BleSensors.Services.Keys.Select(Uuid)];

    public async Task<string?> StartScanAsync()
    {
        var state = await EnsureCentralAsync();
        if (Problem(state) is { } problem)
        {
            return problem;
        }

        _wantScan = true;
        _central!.ScanForPeripherals(ServiceIds, new PeripheralScanningOptions { AllowDuplicatesKey = false });
        IsScanning = true;
        return null;
    }

    public void StopScan()
    {
        _wantScan = false;
        if (IsScanning)
        {
            _central?.StopScan();
            IsScanning = false;
        }
    }

    public void Connect(string deviceId)
    {
        CBPeripheral? peripheral;
        BleDevice? device;
        lock (_gate)
        {
            _peripherals.TryGetValue(deviceId, out peripheral);
            _known.TryGetValue(deviceId, out device);
        }

        if (peripheral is null || device is null || _central is null)
        {
            return;
        }

        remembered.Add(device);
        _central.ConnectPeripheral(peripheral);
    }

    public void Disconnect(string deviceId)
    {
        remembered.Remove(deviceId);
        CBPeripheral? peripheral;
        lock (_gate)
        {
            _peripherals.TryGetValue(deviceId, out peripheral);
        }

        if (peripheral is not null)
        {
            _central?.CancelPeripheralConnection(peripheral);
        }
    }

    public void ReconnectRemembered()
    {
        // Senza sensori ricordati non si crea il gestore: niente richiesta di permesso a chi non usa il Bluetooth.
        if (remembered.Load().Count == 0)
        {
            return;
        }

        _wantReconnect = true;
        if (_central is null)
        {
            _ = EnsureCentralAsync();
        }
        else if (_central.State == CBManagerState.PoweredOn)
        {
            ConnectRemembered();
        }
    }

    private static CBUUID Uuid(ushort shortId) => CBUUID.FromString(shortId.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));

    private static ushort? ShortId(CBUUID? uuid) =>
        uuid is not null && uuid.Data.Length == 2 ? (ushort)((uuid.Data[0] << 8) | uuid.Data[1]) : null;

    private static string? Problem(CBManagerState state) => state switch
    {
        CBManagerState.PoweredOn => null,
        CBManagerState.PoweredOff => "Il Bluetooth è spento: attivarlo dal Centro di Controllo o dalle Impostazioni.",
        CBManagerState.Unauthorized => "L'app non ha il permesso di usare il Bluetooth: Impostazioni → Privacy e sicurezza → Bluetooth.",
        CBManagerState.Unsupported => "Questo dispositivo non supporta il Bluetooth Low Energy.",
        _ => "Il Bluetooth non è pronto: riprovare tra qualche secondo.",
    };

    /// <summary>Crea il gestore al primo uso e attende che lo stato sia noto (acceso, spento, non autorizzato).</summary>
    private async Task<CBManagerState> EnsureCentralAsync()
    {
        if (_central is null)
        {
            _poweredOn = new TaskCompletionSource<CBManagerState>(TaskCreationOptions.RunContinuationsAsynchronously);
            _centralDelegate = new CentralDelegate(this);
            _peripheralDelegate = new PeripheralDelegate(this);
            _central = new CBCentralManager(_centralDelegate, _queue);
        }

        if (_central.State is not (CBManagerState.Unknown or CBManagerState.Resetting))
        {
            return _central.State;
        }

        var known = _poweredOn!.Task;
        return await Task.WhenAny(known, Task.Delay(TimeSpan.FromSeconds(5))) == known ? await known : _central.State;
    }

    private void ConnectRemembered()
    {
        _wantReconnect = false;
        var devices = remembered.Load().ToDictionary(d => d.Id);
        var ids = devices.Keys.Select(id => new NSUuid(id)).ToArray();
        foreach (var peripheral in _central!.RetrievePeripheralsWithIdentifiers(ids))
        {
            var id = peripheral.Identifier.AsString();
            lock (_gate)
            {
                _peripherals[id] = peripheral;
                _known[id] = devices[id];
            }

            // La richiesta resta in attesa finché il sensore non si accende nelle vicinanze.
            _central.ConnectPeripheral(peripheral);
        }
    }

    private void OnState(CBManagerState state)
    {
        _poweredOn?.TrySetResult(state);
        if (state == CBManagerState.PoweredOn)
        {
            if (_wantReconnect)
            {
                ConnectRemembered();
            }

            if (_wantScan && !IsScanning)
            {
                _central!.ScanForPeripherals(ServiceIds, new PeripheralScanningOptions { AllowDuplicatesKey = false });
                IsScanning = true;
            }
        }
        else
        {
            IsScanning = false;
            lock (_gate)
            {
                _connected.Clear();
            }

            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDiscovered(CBPeripheral peripheral, NSDictionary advertisement, int rssi)
    {
        BleSensorType? type = null;
        if (advertisement[CBAdvertisement.DataServiceUUIDsKey] is NSArray services)
        {
            for (nuint i = 0; i < services.Count && type is null; i++)
            {
                if (ShortId(services.GetItem<CBUUID>(i)) is { } shortId && BleSensors.Services.TryGetValue(shortId, out var found))
                {
                    type = found;
                }
            }
        }

        if (type is null)
        {
            return;
        }

        var id = peripheral.Identifier.AsString();
        var name = peripheral.Name ?? (advertisement[CBAdvertisement.DataLocalNameKey] as NSString)?.ToString() ?? "Sensore senza nome";
        var device = new BleDevice(id, name, rssi, type.Value);
        lock (_gate)
        {
            _peripherals[id] = peripheral;
            _known[id] = device;
        }

        DeviceFound?.Invoke(this, device);
    }

    private void OnConnected(CBPeripheral peripheral)
    {
        var id = peripheral.Identifier.AsString();
        lock (_gate)
        {
            if (_known.TryGetValue(id, out var device) && device is not null)
            {
                _connected[id] = device;
            }
        }

        logger.LogInformation("Sensore Bluetooth collegato.");
        peripheral.Delegate = _peripheralDelegate!;
        peripheral.DiscoverServices(ServiceIds);
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisconnected(CBPeripheral peripheral, NSError? error)
    {
        var id = peripheral.Identifier.AsString();
        bool wasConnected;
        lock (_gate)
        {
            wasConnected = _connected.Remove(id);
        }

        if (error is not null)
        {
            logger.LogWarning("Sensore Bluetooth scollegato: {Error}", error.LocalizedDescription);
        }

        // Scollegamento non voluto (fuori portata, batteria): si resta in attesa che torni.
        if (remembered.Load().Any(d => d.Id == id) && _central?.State == CBManagerState.PoweredOn)
        {
            _central.ConnectPeripheral(peripheral);
        }

        if (wasConnected)
        {
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnConnectFailed(NSError? error) =>
        logger.LogWarning("Collegamento al sensore Bluetooth fallito: {Error}", error?.LocalizedDescription);

    private void OnValue(CBCharacteristic characteristic)
    {
        if (characteristic.Value is not { } data || ShortId(characteristic.UUID) is not { } shortId)
        {
            return;
        }

        if (BleSensors.TryParse(shortId, data.ToArray(), 0, out var reading))
        {
            Measurement?.Invoke(this, reading);
        }
    }

    private sealed class CentralDelegate(IosBluetoothSensors owner) : CBCentralManagerDelegate
    {
        public override void UpdatedState(CBCentralManager central) => owner.OnState(central.State);

        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral, NSDictionary advertisementData, NSNumber RSSI) =>
            owner.OnDiscovered(peripheral, advertisementData, RSSI.Int32Value);

        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral) => owner.OnConnected(peripheral);

        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error) =>
            owner.OnDisconnected(peripheral, error);

        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error) =>
            owner.OnConnectFailed(error);
    }

    private sealed class PeripheralDelegate(IosBluetoothSensors owner) : CBPeripheralDelegate
    {
        public override void DiscoveredService(CBPeripheral peripheral, NSError? error)
        {
            foreach (var service in peripheral.Services ?? [])
            {
                peripheral.DiscoverCharacteristics(service);
            }
        }

        public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error)
        {
            foreach (var characteristic in service.Characteristics ?? [])
            {
                if (ShortId(characteristic.UUID) is { } shortId && BleSensors.MeasurementCharacteristics.Contains(shortId))
                {
                    // Vale sia per le notifiche (frequenza cardiaca) sia per le indicazioni (termometro).
                    peripheral.SetNotifyValue(true, characteristic);
                }
            }
        }

        public override void UpdatedCharacterteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error)
        {
            if (error is null)
            {
                owner.OnValue(characteristic);
            }
        }
    }
}
