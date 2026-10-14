using ClinicalSensorFusion.Core.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Bluetooth;
using ClinicalSensorFusion.Core.Sensors;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClinicalSensorFusion.App.ViewModels;

/// <summary>Un sensore nell'elenco: trovato dalla ricerca o già collegato.</summary>
public sealed partial class BluetoothSensorItem(BleDevice device) : ObservableObject
{
    public string Id { get; } = device.Id;

    public string Name { get; } = device.Name;

    public string TypeName { get; } = BleSensors.TypeName(device.Type);

    [ObservableProperty]
    public partial string Signal { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    public partial bool IsConnected { get; set; }

    public string ActionText => IsConnected ? Loc.T("Scollega") : Loc.T("Collega");
}

/// <summary>Ricerca e collegamento dei sensori Bluetooth; i sensori collegati vengono registrati nelle rilevazioni.</summary>
public sealed partial class BluetoothSensorsViewModel(IBluetoothSensors bluetooth) : ObservableObject
{
    public ObservableCollection<BluetoothSensorItem> Devices { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = Loc.T("Accendere il sensore e indossarlo, poi avviare la ricerca.");

    [ObservableProperty]
    public partial string? LiveValue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanText))]
    public partial bool IsScanning { get; set; }

    public string ScanText => IsScanning ? Loc.T("Ferma la ricerca") : Loc.T("Cerca sensori");

    public void Appearing()
    {
        bluetooth.DeviceFound += OnDeviceFound;
        bluetooth.ConnectionChanged += OnConnectionChanged;
        bluetooth.Measurement += OnMeasurement;
        foreach (var device in bluetooth.Connected)
        {
            Upsert(device, connected: true);
        }
    }

    public void Disappearing()
    {
        bluetooth.DeviceFound -= OnDeviceFound;
        bluetooth.ConnectionChanged -= OnConnectionChanged;
        bluetooth.Measurement -= OnMeasurement;
        bluetooth.StopScan();
        IsScanning = false;
    }

    [RelayCommand]
    private async Task ToggleScanAsync()
    {
        if (IsScanning)
        {
            bluetooth.StopScan();
            IsScanning = false;
            Status = Devices.Count == 0 ? Loc.T("Nessun sensore trovato.") : Loc.T("Ricerca ferma.");
            return;
        }

        if (await bluetooth.StartScanAsync() is { } problem)
        {
            Status = problem;
            return;
        }

        IsScanning = true;
        Status = Loc.T("Ricerca in corso: compaiono solo fasce cardio, saturimetri e termometri con profilo Bluetooth standard.");
    }

    [RelayCommand]
    private void Toggle(BluetoothSensorItem item)
    {
        if (item.IsConnected)
        {
            bluetooth.Disconnect(item.Id);
        }
        else
        {
            Status = Loc.F("Collegamento a {0}…", item.Name);
            bluetooth.Connect(item.Id);
        }
    }

    private void OnDeviceFound(object? sender, BleDevice device) =>
        MainThread.BeginInvokeOnMainThread(() => Upsert(device, connected: null));

    private void OnConnectionChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
    {
        var connected = bluetooth.Connected;
        foreach (var item in Devices)
        {
            item.IsConnected = connected.Any(d => d.Id == item.Id);
        }

        foreach (var device in connected)
        {
            Upsert(device, connected: true);
        }

        Status = connected.Count == 0
            ? Loc.T("Nessun sensore collegato.")
            : Loc.F("Collegati: {0}. Le misure entrano nelle prossime rilevazioni.", string.Join(", ", connected.Select(d => d.Name)));
        if (connected.Count == 0)
        {
            LiveValue = null;
        }
    });

    private void OnMeasurement(object? sender, SensorReading reading)
    {
        var c = CultureInfo.CurrentCulture;
        var v = reading.Values;
        var text = reading.Kind switch
        {
            SensorKind.HeartRate => string.Format(c, Loc.T("{0:F0} battiti/min"), v[0]),
            SensorKind.PulseOximeter => string.Format(c, Loc.T("SpO2 {0:F0}% · polso {1:F0} battiti/min"), v[0], v[1]),
            SensorKind.BodyTemperature => string.Format(c, "{0:F1} °C", v[0]),
            _ => null,
        };
        if (text is not null)
        {
            MainThread.BeginInvokeOnMainThread(() => LiveValue = text);
        }
    }

    private void Upsert(BleDevice device, bool? connected)
    {
        var item = Devices.FirstOrDefault(d => d.Id == device.Id);
        if (item is null)
        {
            item = new BluetoothSensorItem(device);
            Devices.Add(item);
        }

        if (device.Rssi != 0)
        {
            item.Signal = string.Create(CultureInfo.CurrentCulture, $"{device.Rssi} dBm");
        }

        if (connected is { } value)
        {
            item.IsConnected = value;
        }
    }
}
