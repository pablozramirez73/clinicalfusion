using ClinicalSensorFusion.Core.Settings;

namespace ClinicalSensorFusion.App.Services;

/// <summary>Impostazioni su <see cref="Preferences"/> (NSUserDefaults) con le stesse chiavi dell'app originale.</summary>
public sealed class MauiAppSettings(IPreferences preferences) : IAppSettings
{
    private const string RecordLocationKey = "capture.location";
    private const string DepthOverlayKey = "capture.depth_overlay";

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

    public bool ShowDepthOverlay
    {
        get => preferences.Get(DepthOverlayKey, true);
        set => preferences.Set(DepthOverlayKey, value);
    }
}
