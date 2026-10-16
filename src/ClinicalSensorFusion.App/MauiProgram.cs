using ClinicalSensorFusion.App.Controls;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.App.ViewModels;
using ClinicalSensorFusion.App.Views;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Security;
using ClinicalSensorFusion.Core.Settings;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Core.Telemetry;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
#if IOS
using ClinicalSensorFusion.App.Platforms.iOS.Bluetooth;
using ClinicalSensorFusion.App.Platforms.iOS.Rendering;
using ClinicalSensorFusion.App.Platforms.iOS.Security;
using ClinicalSensorFusion.App.Platforms.iOS.Services;
#elif ANDROID
using ClinicalSensorFusion.App.Platforms.Android.Rendering;
using ClinicalSensorFusion.App.Platforms.Android.Security;
using ClinicalSensorFusion.App.Platforms.Android.Services;
#endif

namespace ClinicalSensorFusion.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        _ = AppDiagnostics.SinceLaunch; // fa partire il cronometro dell'avvio

        // Lingua dell'interfaccia: va fissata prima che si carichi qualunque XAML, perché {l:Tr} traduce una
        // volta sola, quando la pagina viene creata. Per questo cambiarla dalle Impostazioni vale dal riavvio.
        Loc.Use(Loc.Resolve(Preferences.Default.Get(SettingsKeys.UiLanguage, string.Empty), System.Globalization.CultureInfo.CurrentUICulture));
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureMauiHandlers(handlers =>
            {
#if IOS || ANDROID
                handlers.AddHandler<CapturePreview, CapturePreviewHandler>();
#endif
            });

#if IOS
        // I campi di testo nativi hanno un bordo proprio: lo toglie, il riquadro lo disegna FormField.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("Borderless", (handler, _) =>
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None);
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("Borderless", (handler, _) =>
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None);
#elif ANDROID
        // Idem per la sottolineatura Material dei campi Android.
        var noUnderline = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("Borderless", (handler, _) =>
            handler.PlatformView.BackgroundTintList = noUnderline);
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("Borderless", (handler, _) =>
            handler.PlatformView.BackgroundTintList = noUnderline);
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("Borderless", (handler, _) =>
            handler.PlatformView.BackgroundTintList = noUnderline);
#endif

#if DEBUG
        builder.Logging.AddDebug().SetMinimumLevel(LogLevel.Debug);
#endif

        var services = builder.Services;

        // Diagnostica: log su file a rotazione (anche nelle build di rilascio, dove non c'è altro) e metriche.
        var fileLogs = new RollingFileLoggerProvider(AppDiagnostics.FileOptions);
        builder.Logging.AddProvider(fileLogs);
        services.AddSingleton(fileLogs);
        services.AddSingleton(new MetricsCollector());
        services.AddSingleton<AppDiagnostics>();

        // Impostazioni e sicurezza
        services.AddSingleton(Preferences.Default);
        services.AddSingleton(SecureStorage.Default);
        services.AddSingleton<MauiAppSettings>();
        services.AddSingleton<AppLockController>();
        services.AddSingleton<IAppSettings>(sp => sp.GetRequiredService<MauiAppSettings>());

        // Persistenza: un'unica connessione cifrata per tutta l'app.
        services.AddSingleton(sp => new ClinicalDatabase(
            AppPaths.Database,
            sp.GetRequiredService<IDatabaseKeyProvider>(),
            sp.GetRequiredService<ILogger<ClinicalDatabase>>()));
        services.AddSingleton<ClinicalRepository>();

        // Modello linguistico. Timeout ampio: un modello locale può impiegare minuti su prompt lunghi;
        // l'annullamento è affidato al CancellationToken della UI.
        var ollama = services.AddHttpClient<OllamaClient>(client => client.Timeout = TimeSpan.FromMinutes(10))
            .AddHttpMessageHandler(sp => new ScopedCredentialsHandler(_ =>
                CloudflareAccess.CredentialsAsync(sp.GetRequiredService<MauiAppSettings>(), sp.GetRequiredService<ISecureStorage>())));

        // Sincronizzazione con il server clinico.
        var sync = services.AddHttpClient<ClinicalSyncClient>(client => client.Timeout = TimeSpan.FromMinutes(5))
            .AddHttpMessageHandler(sp => new ScopedCredentialsHandler(_ =>
                SyncService.CredentialsAsync(sp.GetRequiredService<MauiAppSettings>(), sp.GetRequiredService<ISecureStorage>())));

        // Su tutte le piattaforme: niente HTTP in chiaro fuori dalla rete locale e niente reindirizzamenti
        // seguiti in automatico, che porterebbero le credenziali verso un altro host. Un 3xx arriva al client
        // come errore, con il suo codice.
#if ANDROID
        // Su iOS i cookie della WebView li condivide il sistema; qui i client usano un contenitore comune,
        // riempito dal login nella WebView.
        services.AddSingleton<System.Net.CookieContainer>();
#endif
        services.AddTransient<LocalCleartextHandler>();
        foreach (var client in (IHttpClientBuilder[])[ollama, sync])
        {
            client.AddHttpMessageHandler<LocalCleartextHandler>()
#if ANDROID
                .ConfigurePrimaryHttpMessageHandler(sp => new Xamarin.Android.Net.AndroidMessageHandler
                {
                    CookieContainer = sp.GetRequiredService<System.Net.CookieContainer>(),
                    AllowAutoRedirect = false,
                });
#elif IOS
                .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.NSUrlSessionHandler { AllowAutoRedirect = false });
#else
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
#endif
        }

        services.AddTransient<SessionSynchronizer>();
        services.AddSingleton<SyncService>();

        // Acquisizione
        services.AddSingleton<CaptureCoordinator>();
        services.AddSingleton<IDialogService, ShellDialogService>();
        services.AddSingleton(TextToSpeech.Default);
        services.AddSingleton<ISpeechGuide, TextToSpeechGuide>();

#if IOS
        services.AddSingleton<IDatabaseKeyProvider, KeychainKeyProvider>();
        services.AddSingleton<IDeviceAuthenticator, BiometricAuthenticator>();
        services.AddSingleton<ILockScreen, IosLockScreen>();
        services.AddSingleton<ICaptureEngine, IosCaptureEngine>();
        services.AddSingleton<IWebSessionBridge, IosWebSessionBridge>();
        services.AddSingleton<IAudioPlayback, IosAudioPlayback>();
        services.AddSingleton<IBluetoothSensors, IosBluetoothSensors>();
#elif ANDROID
        services.AddSingleton<IDatabaseKeyProvider, KeystoreKeyProvider>();
        services.AddSingleton<IDeviceAuthenticator, AndroidDeviceAuthenticator>();
        services.AddSingleton<ILockScreen, AndroidLockScreen>();
        services.AddSingleton<ICaptureEngine, AndroidCaptureEngine>();
        services.AddSingleton<IWebSessionBridge, AndroidWebSessionBridge>();
        services.AddSingleton<IAudioPlayback, AndroidAudioPlayback>();
        // Sensori Bluetooth: per ora solo su iOS.
        services.AddSingleton<IBluetoothSensors, NullBluetoothSensors>();
#endif

        services.AddSingleton<RememberedBleDevices>();

        // UI
        services.AddSingleton<AppShell>();
        services.AddTransient<BluetoothSensorsViewModel>();
        services.AddTransient<BluetoothSensorsPage>();
        services.AddTransient<SessionsViewModel>();
        services.AddTransient<SessionsPage>();
        // Singleton: una sola schermata di acquisizione, il cui stato (registrazione in corso) deve
        // sopravvivere al cambio di scheda. Evita anche che il container trattenga istanze IDisposable transient.
        services.AddSingleton<CaptureViewModel>();
        services.AddSingleton<CapturePage>();
        services.AddTransient<SessionDetailViewModel>();
        services.AddTransient<SessionDetailPage>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<RemoteAccessViewModel>();
        services.AddTransient<RemoteAccessPage>();

        var app = builder.Build();
        app.Services.GetRequiredService<AppDiagnostics>().HookUnhandledExceptions();

        // Server clinico preimpostato in fase di compilazione, se presente (non blocca l'avvio).
        _ = BuildProvisioning.ApplyAsync(
            app.Services.GetRequiredService<MauiAppSettings>(),
            app.Services.GetRequiredService<ISecureStorage>(),
            app.Services.GetRequiredService<IPreferences>());
        return app;
    }
}
