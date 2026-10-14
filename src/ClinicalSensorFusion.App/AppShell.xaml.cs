using ClinicalSensorFusion.App.Views;

namespace ClinicalSensorFusion.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(SessionDetailPage), typeof(SessionDetailPage));
        Routing.RegisterRoute(nameof(RemoteAccessPage), typeof(RemoteAccessPage));
        Routing.RegisterRoute(nameof(BluetoothSensorsPage), typeof(BluetoothSensorsPage));
    }
}
