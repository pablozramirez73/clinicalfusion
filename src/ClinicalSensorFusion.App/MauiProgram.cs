using ClinicalSensorFusion.App.Controls;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.App.ViewModels;
using ClinicalSensorFusion.App.Views;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Settings;
using ClinicalSensorFusion.Core.Sync;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
#if IOS
using ClinicalSensorFusion.App.Platforms.iOS.Rendering;
using ClinicalSensorFusion.App.Platforms.iOS.Security;
using ClinicalSensorFusion.App.Platforms.iOS.Services;
#endif

namespace ClinicalSensorFusion.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureMauiHandlers(handlers =>
            {
#if IOS
                handlers.AddHandler<CapturePreview, CapturePreviewHandler>();
#endif
            });

#if IOS
        // I campi di testo nativi hanno un bordo proprio: lo toglie, il riquadro lo disegna FormField.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("Borderless", (handler, _) =>
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None);
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("Borderless", (handler, _) =>
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None);
#endif

#if DEBUG
        builder.Logging.AddDebug().SetMinimumLevel(LogLevel.Debug);
#endif

        var services = builder.Services;

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
        services.AddHttpClient<OllamaClient>(client => client.Timeout = TimeSpan.FromMinutes(10))
            .AddHttpMessageHandler<CloudflareAccessHandler>();

        // Sincronizzazione con il server clinico.
        services.AddTransient<DeviceApiKeyHandler>();
        services.AddHttpClient<ClinicalSyncClient>(client => client.Timeout = TimeSpan.FromMinutes(5))
            .AddHttpMessageHandler<DeviceApiKeyHandler>();
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

        // Server clinico preimpostato in fase di compilazione, se presente (non blocca l'avvio).
        _ = BuildProvisioning.ApplyAsync(
            app.Services.GetRequiredService<MauiAppSettings>(),
            app.Services.GetRequiredService<ISecureStorage>(),
            app.Services.GetRequiredService<IPreferences>());
        return app;
    }
}
