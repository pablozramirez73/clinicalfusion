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
    IDialogService dialogs) : ObservableObject
{
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

    [ObservableProperty]
    public partial string? CloudflareClientId { get; set; }

    [ObservableProperty]
    public partial string? CloudflareClientSecret { get; set; }

    [ObservableProperty]
    public partial bool RecordLocation { get; set; }

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
        RecordLocation = settings.RecordLocation;
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
        settings.RecordLocation = RecordLocation;
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
