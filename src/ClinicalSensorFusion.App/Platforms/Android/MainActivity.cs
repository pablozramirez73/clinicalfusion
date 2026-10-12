using Android.App;
using Android.Content.PM;
using Android.Views;
using ClinicalSensorFusion.App.Services;

namespace ClinicalSensorFusion.App;

// Solo verticale, come su iOS: overlay e orientamento dei punti assumono il telefono in verticale.
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnResume()
    {
        base.OnResume();
        ApplyPrivacy();
    }

    protected override void OnPause()
    {
        ApplyPrivacy();
        base.OnPause();
    }

    /// <summary>
    /// Con il blocco dell'app attivo il contenuto non deve comparire nell'anteprima delle app recenti né
    /// negli screenshot: la copertura del blocco arriva solo dopo che il sistema ha già scattato l'anteprima.
    /// </summary>
    private void ApplyPrivacy()
    {
        var settings = IPlatformApplication.Current?.Services.GetService<MauiAppSettings>();
        if (settings?.RequireDeviceAuthentication == true)
        {
            Window?.AddFlags(WindowManagerFlags.Secure);
        }
        else
        {
            Window?.ClearFlags(WindowManagerFlags.Secure);
        }
    }
}
