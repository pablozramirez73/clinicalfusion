using ClinicalSensorFusion.Core.Settings;

namespace ClinicalSensorFusion.App.Services;

/// <summary>Impostazioni su <see cref="Preferences"/> (NSUserDefaults) con le stesse chiavi dell'app originale.</summary>
public sealed class MauiAppSettings(IPreferences preferences) : IAppSettings
{
    private const string RecordLocationKey = "capture.location";
    private const string DepthOverlayKey = "capture.depth_overlay";
    private const string SyncServerKey = "sync.server";
    private const string AutoSyncKey = "sync.auto";

    public string OllamaServer
    {
        get => preferences.Get(SettingsKeys.OllamaServer, string.Empty);
        set => preferences.Set(SettingsKeys.OllamaServer, value?.Trim() ?? string.Empty);
    }

    public string OllamaModel
    {
        get => preferences.Get(SettingsKeys.OllamaModel, SettingsDefaults.OllamaModel);
        set => preferences.Set(SettingsKeys.OllamaModel, value?.Trim() ?? SettingsDefaults.OllamaModel);
    }

    public string OllamaLanguage
    {
        get => preferences.Get(SettingsKeys.OllamaLanguage, SettingsDefaults.OllamaLanguage);
        set => preferences.Set(SettingsKeys.OllamaLanguage, value ?? SettingsDefaults.OllamaLanguage);
    }

    public string OperatorId
    {
        get => preferences.Get(SettingsKeys.OperatorId, string.Empty);
        set => preferences.Set(SettingsKeys.OperatorId, value?.Trim() ?? string.Empty);
    }

    public string OperatorName
    {
        get => preferences.Get(SettingsKeys.OperatorName, string.Empty);
        set => preferences.Set(SettingsKeys.OperatorName, value?.Trim() ?? string.Empty);
    }

    /// <summary>Registrazione GPS: disattivata per default (minimizzazione dei dati).</summary>
    public bool RecordLocation
    {
        get => preferences.Get(RecordLocationKey, false);
        set => preferences.Set(RecordLocationKey, value);
    }

    /// <summary>Indirizzo del server clinico (es. https://clinica.esempio.it).</summary>
    public string SyncServer
    {
        get => preferences.Get(SyncServerKey, string.Empty);
        set => preferences.Set(SyncServerKey, value?.Trim() ?? string.Empty);
    }

    /// <summary>Invio automatico al termine di ogni rilevazione.</summary>
    public bool AutoSync
    {
        get => preferences.Get(AutoSyncKey, true);
        set => preferences.Set(AutoSyncKey, value);
    }

    public bool ShowDepthOverlay
    {
        get => preferences.Get(DepthOverlayKey, true);
        set => preferences.Set(DepthOverlayKey, value);
    }
}
