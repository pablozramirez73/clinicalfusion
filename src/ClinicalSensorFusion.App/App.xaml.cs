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
        var window = new Window(FirstPage()) { Title = "SensorFusion Clinic" };
        _services.GetRequiredService<AppLockController>().Attach(window);
        _services.GetRequiredService<AppDiagnostics>().Attach(window);
        // I sensori Bluetooth già associati si ricollegano da soli quando sono accesi.
        window.Created += (_, _) => _services.GetRequiredService<IBluetoothSensors>().ReconnectRemembered();
        return window;
    }

    /// <summary>
    /// Dopo l'installazione si sceglie prima la lingua: la shell si crea solo dopo, già nella lingua scelta
    /// (i testi delle pagine si traducono una volta sola, alla creazione). Chi aveva già configurato l'app
    /// prima che questa pagina esistesse non la vede.
    /// </summary>
    private Page FirstPage()
    {
        var settings = _services.GetRequiredService<MauiAppSettings>();
        if (settings.SetupCompleted || !string.IsNullOrWhiteSpace(settings.OperatorId))
        {
            settings.SetupCompleted = true;
            return _services.GetRequiredService<AppShell>();
        }

        var setup = new Views.SetupPage();
        setup.Completed += (_, language) =>
        {
            settings.UiLanguage = language;
            settings.SetupCompleted = true;
            if (Windows.Count > 0)
            {
                Windows[0].Page = _services.GetRequiredService<AppShell>();
            }
        };
        return setup;
    }
}
