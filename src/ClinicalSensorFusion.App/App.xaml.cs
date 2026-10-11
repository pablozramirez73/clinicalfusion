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
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(_services.GetRequiredService<AppShell>()) { Title = "SensorFusion Clinic" };
}
