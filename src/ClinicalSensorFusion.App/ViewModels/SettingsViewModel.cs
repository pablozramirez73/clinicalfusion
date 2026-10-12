using System.Collections.ObjectModel;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.App.Views;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClinicalSensorFusion.App.ViewModels;

/// <summary>Operatore, server Ollama (indirizzo, modello, lingua), accesso remoto e privacy.</summary>
public sealed partial class SettingsViewModel(
    MauiAppSettings settings,
    ISecureStorage secureStorage,
    OllamaClient ollama,
    AppLockController appLock,
    IDialogService dialogs) : ObservableObject
{
    private bool _loadingLock;

    /// <summary>Blocco dell'app con riconoscimento biometrico o codice. Ha effetto subito, non con «Salva»: è una scelta di sicurezza.</summary>
    [ObservableProperty]
    public partial bool RequireUnlock { get; set; }

    public string UnlockMethod => appLock.Authenticator.MethodName;

    public string AppVersion => $"SensorFusion Clinic {AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})";

    async partial void OnRequireUnlockChanged(bool value)
    {
        if (_loadingLock || value == appLock.IsEnabled)
        {
            return;
        }

        if (await appLock.SetEnabledAsync(value))
        {
            return;
        }

        // Non riconosciuto o non disponibile: l'interruttore torna com'era.
        _loadingLock = true;
        RequireUnlock = appLock.IsEnabled;
        _loadingLock = false;
        if (!appLock.Authenticator.IsAvailable)
        {
            await dialogs.AlertAsync("Blocco dell'app", "Su questo telefono non sono impostati né il riconoscimento biometrico né un codice di sblocco: attivali nelle impostazioni di sistema.");
        }
    }

    public IReadOnlyList<string> Languages { get; } = SettingsDefaults.Languages;

    public ObservableCollection<string> Models { get; } = [];

    [ObservableProperty]
    public partial string OperatorId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OperatorName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OllamaServer { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OllamaModel { get; set; } = SettingsDefaults.OllamaModel;

    [ObservableProperty]
    public partial string OllamaLanguage { get; set; } = SettingsDefaults.OllamaLanguage;

    /// <summary>Istruzioni aggiuntive per il modello: si sommano alle regole predefinite dell'app.</summary>
    [ObservableProperty]
    public partial string OllamaPrompt { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? CloudflareClientId { get; set; }

    [ObservableProperty]
    public partial string? CloudflareClientSecret { get; set; }

    [ObservableProperty]
    public partial bool RecordLocation { get; set; }

    [ObservableProperty]
    public partial string SyncServer { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SyncApiKey { get; set; }

    [ObservableProperty]
    public partial bool AutoSync { get; set; }

    [ObservableProperty]
    public partial string? ConnectionStatus { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    public async Task LoadAsync()
    {
        OperatorId = settings.OperatorId;
        OperatorName = settings.OperatorName;
        OllamaServer = settings.OllamaServer;
        OllamaModel = settings.OllamaModel;
        OllamaLanguage = settings.OllamaLanguage;
        OllamaPrompt = settings.OllamaPrompt;
        RecordLocation = settings.RecordLocation;
        _loadingLock = true;
        RequireUnlock = appLock.IsEnabled;
        _loadingLock = false;
        SyncServer = settings.SyncServer;
        AutoSync = settings.AutoSync;
        SyncApiKey = await secureStorage.GetAsync(SyncService.ApiKeyStorageKey);
        CloudflareClientId = await secureStorage.GetAsync(SettingsKeys.CloudflareClientId);
        CloudflareClientSecret = await secureStorage.GetAsync(SettingsKeys.CloudflareClientSecret);
        if (!Models.Contains(OllamaModel))
        {
            Models.Add(OllamaModel);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(OperatorId) || string.IsNullOrWhiteSpace(OperatorName))
        {
            await dialogs.AlertAsync("Impostazioni", "Identificativo e nome dell'operatore sono obbligatori: ogni rilevazione viene firmata dall'operatore.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(OllamaServer) && !OllamaEndpoint.TryParse(OllamaServer, out _))
        {
            await dialogs.AlertAsync("Impostazioni", "Indirizzo del server Ollama non valido (es. 192.168.1.20 oppure https://ai.esempio.it).");
            return;
        }

        settings.OperatorId = OperatorId;
        settings.OperatorName = OperatorName;
        settings.OllamaServer = OllamaServer;
        settings.OllamaModel = OllamaModel;
        settings.OllamaLanguage = OllamaLanguage;
        settings.OllamaPrompt = OllamaPrompt;
        if (!string.IsNullOrWhiteSpace(SyncServer) &&
            (!Uri.TryCreate(SyncServer.Trim(), UriKind.Absolute, out var syncUri) || syncUri.Scheme != Uri.UriSchemeHttps && !syncUri.IsLoopback && !IsPrivateHost(syncUri.Host)))
        {
            await dialogs.AlertAsync("Impostazioni", "Il server clinico deve usare HTTPS (HTTP è ammesso solo in rete locale).");
            return;
        }

        settings.RecordLocation = RecordLocation;
        settings.SyncServer = SyncServer;
        settings.AutoSync = AutoSync;
        await SaveSecretAsync(SyncService.ApiKeyStorageKey, SyncApiKey);
        await SaveSecretAsync(SettingsKeys.CloudflareClientId, CloudflareClientId);
        await SaveSecretAsync(SettingsKeys.CloudflareClientSecret, CloudflareClientSecret);
        await dialogs.AlertAsync("Impostazioni", "Salvate.");
    }

    /// <summary>Come l'app originale: GET /api/tags per verificare il server ed elencare i modelli.</summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!OllamaEndpoint.TryParse(OllamaServer, out var server) || server is null)
        {
            ConnectionStatus = "Indirizzo non valido.";
            return;
        }

        IsTesting = true;
        ConnectionStatus = $"Connessione a {server}…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var models = await ollama.GetModelsAsync(server, cts.Token);
            Models.Clear();
            foreach (var m in models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                Models.Add(m.Name);
            }

            if (!Models.Contains(OllamaModel) && Models.Count > 0)
            {
                OllamaModel = Models[0];
            }

            ConnectionStatus = $"OK: {Models.Count} modelli disponibili.";
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "Timeout: il server non risponde (stessa rete Wi-Fi? Ollama avviato con OLLAMA_HOST=0.0.0.0?).";
        }
        catch (Exception ex)
        {
            ConnectionStatus = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private Task OpenRemoteAccessAsync() =>
        Shell.Current.GoToAsync(nameof(RemoteAccessPage), new Dictionary<string, object> { ["Url"] = OllamaServer });

    [RelayCommand]
    private void GenerateOperatorId()
    {
        if (string.IsNullOrWhiteSpace(OperatorId))
        {
            OperatorId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        }
    }

    private static bool IsPrivateHost(string host) =>
        System.Net.IPAddress.TryParse(host, out var ip) && ip.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..]
        || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    private async Task SaveSecretAsync(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            secureStorage.Remove(key);
        }
        else
        {
            await secureStorage.SetAsync(key, value.Trim());
        }
    }
}
