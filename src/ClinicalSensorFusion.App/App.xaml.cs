using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.App.Services;

namespace ClinicalSensorFusion.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    // La shell si risolve qui e non nel costruttore: il suo XAML usa le risorse caricate da InitializeComponent.
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<AppShell>()) { Title = "SensorFusion Clinic" };
        _services.GetRequiredService<AppLockController>().Attach(window);
        _services.GetRequiredService<AppDiagnostics>().Attach(window);
        // I sensori Bluetooth già associati si ricollegano da soli quando sono accesi.
        window.Created += (_, _) => _services.GetRequiredService<IBluetoothSensors>().ReconnectRemembered();
        return window;
    }
}
