using ClinicalSensorFusion.App.Controls;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.App.ViewModels;
using ClinicalSensorFusion.App.Views;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Settings;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Core.Telemetry;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
#if IOS
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
        services.AddTransient<CloudflareAccessHandler>();
        var ollama = services.AddHttpClient<OllamaClient>(client => client.Timeout = TimeSpan.FromMinutes(10))
            .AddHttpMessageHandler<CloudflareAccessHandler>();

        // Sincronizzazione con il server clinico.
        services.AddTransient<DeviceApiKeyHandler>();
        var sync = services.AddHttpClient<ClinicalSyncClient>(client => client.Timeout = TimeSpan.FromMinutes(5))
            .AddHttpMessageHandler<DeviceApiKeyHandler>();

#if ANDROID
        // Su iOS cookie della WebView e limite dell'HTTP alla rete locale li gestisce il sistema; qui no:
        // i client condividono un contenitore di cookie (riempito dal login nella WebView) e un controllo esplicito.
        services.AddSingleton<System.Net.CookieContainer>();
        services.AddTransient<LocalCleartextHandler>();
        foreach (var client in (IHttpClientBuilder[])[ollama, sync])
        {
            client.AddHttpMessageHandler<LocalCleartextHandler>()
                .ConfigurePrimaryHttpMessageHandler(sp => new Xamarin.Android.Net.AndroidMessageHandler
                {
                    CookieContainer = sp.GetRequiredService<System.Net.CookieContainer>(),
                });
        }
#endif
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
#elif ANDROID
        services.AddSingleton<IDatabaseKeyProvider, KeystoreKeyProvider>();
        services.AddSingleton<IDeviceAuthenticator, AndroidDeviceAuthenticator>();
        services.AddSingleton<ILockScreen, AndroidLockScreen>();
        services.AddSingleton<ICaptureEngine, AndroidCaptureEngine>();
        services.AddSingleton<IWebSessionBridge, AndroidWebSessionBridge>();
        services.AddSingleton<IAudioPlayback, AndroidAudioPlayback>();
#endif

        // UI
        services.AddSingleton<AppShell>();
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
