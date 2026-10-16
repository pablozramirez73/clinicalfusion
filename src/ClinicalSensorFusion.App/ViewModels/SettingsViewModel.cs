using System.Collections.ObjectModel;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.App.Views;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Security;
using ClinicalSensorFusion.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClinicalSensorFusion.App.ViewModels;

/// <summary>Voce della scelta della lingua dell'interfaccia. <see cref="Code"/> vuoto = la lingua del telefono.</summary>
public sealed record UiLanguageOption(string Code, string Name);

/// <summary>Operatore, lingua dell'interfaccia, server Ollama (indirizzo, modello, lingua), accesso remoto e privacy.</summary>
public sealed partial class SettingsViewModel(
    MauiAppSettings settings,
    ISecureStorage secureStorage,
    OllamaClient ollama,
    AppLockController appLock,
    AppDiagnostics diagnostics,
    IDialogService dialogs) : ObservableObject
{
    private bool _loadingLock;

    // Valori sensibili come sono salvati: un salvataggio che li cambia chiede conferma.
    private SensitiveSettings _savedSensitive = new(null, null, null, null, null);

    /// <summary>Blocco dell'app con riconoscimento biometrico o codice. Ha effetto subito, non con «Salva»: è una scelta di sicurezza.</summary>
    [ObservableProperty]
    public partial bool RequireUnlock { get; set; }

    public string UnlockMethod => appLock.Authenticator.MethodName;

    public string UnlockDescription =>
        Loc.F("Chiedi {0} all'apertura e al ritorno dal background. Attivo subito, senza «Salva».", Loc.T(UnlockMethod));

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
            await dialogs.AlertAsync(Loc.T("Blocco dell'app"), Loc.T("Su questo telefono non sono impostati né il riconoscimento biometrico né un codice di sblocco: attivali nelle impostazioni di sistema."));
        }
    }

    public IReadOnlyList<string> Languages { get; } = SettingsDefaults.Languages;

    /// <summary>«Come il telefono» e poi le lingue disponibili, ciascuna col proprio nome (non tradotto: va riconosciuto da chi la parla).</summary>
    public IReadOnlyList<UiLanguageOption> UiLanguages { get; } =
        [new(string.Empty, Loc.T("Come il telefono")), .. Loc.Supported.Select(l => new UiLanguageOption(l.Code, l.Name))];

    [ObservableProperty]
    public partial UiLanguageOption? UiLanguage { get; set; }

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

    /// <summary>Impronta del certificato del server clinico (vuota = certificato di un'autorità).</summary>
    [ObservableProperty]
    public partial string? SyncServerPin { get; set; }

    [ObservableProperty]
    public partial bool AutoSync { get; set; }

    [ObservableProperty]
    public partial bool ShowFallRiskEstimate { get; set; }

    [ObservableProperty]
    public partial string? ConnectionStatus { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    public async Task LoadAsync()
    {
        OperatorId = settings.OperatorId;
        OperatorName = settings.OperatorName;
        UiLanguage = UiLanguages.FirstOrDefault(l => l.Code == settings.UiLanguage) ?? UiLanguages[0];
        OllamaServer = settings.OllamaServer;
        OllamaModel = settings.OllamaModel;
        OllamaLanguage = settings.OllamaLanguage;
        OllamaPrompt = settings.OllamaPrompt;
        RecordLocation = settings.RecordLocation;
        _loadingLock = true;
        RequireUnlock = appLock.IsEnabled;
        _loadingLock = false;
        SyncServer = settings.SyncServer;
        SyncServerPin = settings.SyncServerPin;
        AutoSync = settings.AutoSync;
        ShowFallRiskEstimate = settings.ShowFallRiskEstimate;
        SyncApiKey = await secureStorage.GetAsync(SyncService.ApiKeyStorageKey);
        CloudflareClientId = await secureStorage.GetAsync(SettingsKeys.CloudflareClientId);
        CloudflareClientSecret = await secureStorage.GetAsync(SettingsKeys.CloudflareClientSecret);
        _savedSensitive = CurrentSensitive();
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
            await dialogs.AlertAsync(Loc.T("Impostazioni"), Loc.T("Identificativo e nome dell'operatore sono obbligatori: ogni rilevazione viene firmata dall'operatore."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(OllamaServer) && !OllamaEndpoint.TryParse(OllamaServer, out _))
        {
            await dialogs.AlertAsync(Loc.T("Impostazioni"), Loc.T("Indirizzo del server Ollama non valido (es. 192.168.1.20 oppure https://ai.esempio.it)."));
            return;
        }

        // Prima tutti i controlli, poi le scritture: un salvataggio rifiutato non lascia impostazioni a metà.
        if (!string.IsNullOrWhiteSpace(SyncServer) &&
            (!Uri.TryCreate(SyncServer.Trim(), UriKind.Absolute, out var syncUri) || syncUri.Scheme != Uri.UriSchemeHttps))
        {
            await dialogs.AlertAsync(Loc.T("Impostazioni"), Loc.T("Il server clinico deve usare HTTPS."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(SyncServerPin) && !CertificatePin.IsWellFormed(SyncServerPin))
        {
            await dialogs.AlertAsync(Loc.T("Impostazioni"), Loc.T("Impronta del certificato non valida: deve essere un SHA-256 in base64, come la mostra lo script del server."));
            return;
        }

        if (OllamaEndpoint.TryParse(OllamaServer, out var ollamaUri) && ollamaUri!.Scheme != Uri.UriSchemeHttps)
        {
            await dialogs.AlertAsync(Loc.T("Impostazioni"), Loc.T("Il server Ollama deve usare HTTPS. Per un Ollama in rete locale usa «IA tramite il server clinico»."));
            return;
        }

        // Dove vanno i dati clinici e con quali credenziali: chi cambia questi valori deve farsi riconoscere.
        var sensitive = CurrentSensitive();
        if (sensitive.DiffersFrom(_savedSensitive)
            && !await appLock.ConfirmSensitiveAsync(Loc.T("Conferma la modifica di server e credenziali")))
        {
            await dialogs.AlertAsync(Loc.T("Impostazioni"), Loc.T("Impostazioni non salvate: per cambiare server o credenziali serve farsi riconoscere."));
            return;
        }

        settings.OperatorId = OperatorId;
        settings.OperatorName = OperatorName;
        settings.OllamaServer = OllamaServer;
        settings.OllamaModel = OllamaModel;
        settings.OllamaLanguage = OllamaLanguage;
        settings.OllamaPrompt = OllamaPrompt;

        // La lingua scelta vale dal prossimo avvio: le pagine già create restano nella lingua di adesso.
        var languageChanged = UiLanguage is { } language && language.Code != settings.UiLanguage;
        if (UiLanguage is { } selected)
        {
            settings.UiLanguage = selected.Code;
        }

        settings.RecordLocation = RecordLocation;
        settings.SyncServer = SyncServer;
        settings.SyncServerPin = CertificatePin.Normalize(SyncServerPin) ?? string.Empty;
        settings.AutoSync = AutoSync;
        settings.ShowFallRiskEstimate = ShowFallRiskEstimate;
        await SaveSecretAsync(SyncService.ApiKeyStorageKey, SyncApiKey);
        await SaveSecretAsync(SettingsKeys.CloudflareClientId, CloudflareClientId);
        await SaveSecretAsync(SettingsKeys.CloudflareClientSecret, CloudflareClientSecret);
        _savedSensitive = sensitive;
        await dialogs.AlertAsync(Loc.T("Impostazioni"), languageChanged
            ? Loc.T("Salvate. La nuova lingua si applica al prossimo avvio dell'app.")
            : Loc.T("Salvate."));
    }

    /// <summary>Come l'app originale: GET /api/tags per verificare il server ed elencare i modelli.</summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!OllamaEndpoint.TryParse(OllamaServer, out var server) || server is null)
        {
            ConnectionStatus = Loc.T("Indirizzo non valido.");
            return;
        }

        IsTesting = true;
        ConnectionStatus = Loc.F("Connessione a {0}…", server);
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

            ConnectionStatus = Loc.F("OK: {0} modelli disponibili.", Models.Count);
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = Loc.T("Timeout: il server non risponde. Controllare che l'app abbia il permesso «Rete locale» (Impostazioni del telefono → Privacy e sicurezza → Rete locale), che telefono e server siano sulla stessa rete e che Ollama sia avviato con OLLAMA_HOST=0.0.0.0.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException
        {
            SocketErrorCode: System.Net.Sockets.SocketError.HostUnreachable or System.Net.Sockets.SocketError.NetworkUnreachable,
        })
        {
            // Su iOS è il modo in cui si presenta il permesso «Rete locale» negato o non ancora concesso.
            ConnectionStatus = Loc.T("Server non raggiungibile: il telefono non ha accesso alla rete locale. Attivare «Rete locale» per questa app in Impostazioni del telefono → Privacy e sicurezza → Rete locale, poi riprovare.");
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

    /// <summary>
    /// Condivide i log diagnostici (eventi tecnici e metriche, senza dati dei pazienti): è l'operatore a
    /// scegliere a chi mandarli, l'app non li invia mai da sola.
    /// </summary>
    [RelayCommand]
    private async Task ShareLogsAsync()
    {
        try
        {
            var logs = await diagnostics.ExportAsync();
            if (logs.Count == 0)
            {
                await dialogs.AlertAsync(Loc.T("Log diagnostici"), Loc.T("Non ci sono ancora log da condividere."));
                return;
            }

            await Share.Default.RequestAsync(new ShareMultipleFilesRequest
            {
                Title = Loc.T("Log diagnostici"),
                Files = [.. logs.Select(path => new ShareFile(path, "application/x-ndjson"))],
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await dialogs.AlertAsync(Loc.T("Log diagnostici"), ex.Message);
        }
    }

    [RelayCommand]
    private Task OpenBluetoothSensorsAsync() => Shell.Current.GoToAsync(nameof(BluetoothSensorsPage));

    [RelayCommand]
    private Task OpenRemoteAccessAsync() =>
        Shell.Current.GoToAsync(nameof(RemoteAccessPage), new Dictionary<string, object> { ["Url"] = OllamaServer });

    /// <summary>
    /// Ollama attraverso il server clinico: l'app parla solo con il server, in HTTPS e con la chiave del
    /// dispositivo, e il server inoltra a Ollama. È il modo di usare un Ollama in rete locale senza HTTP in chiaro.
    /// </summary>
    [RelayCommand]
    private async Task UseClinicalServerForAiAsync()
    {
        if (!Uri.TryCreate(SyncServer?.Trim(), UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps)
        {
            await dialogs.AlertAsync(Loc.T("Impostazioni"), Loc.T("Prima inserisci l'indirizzo HTTPS del server clinico."));
            return;
        }

        OllamaServer = new Uri(server.AbsoluteUri.EndsWith('/') ? server : new Uri(server.AbsoluteUri + "/"), "api/v1/ai/").AbsoluteUri;
    }

    [RelayCommand]
    private void GenerateOperatorId()
    {
        if (string.IsNullOrWhiteSpace(OperatorId))
        {
            OperatorId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        }
    }

    private SensitiveSettings CurrentSensitive() =>
        new(SyncServer, SyncApiKey, OllamaServer, CloudflareClientId, CloudflareClientSecret, CertificatePin.Normalize(SyncServerPin));

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
