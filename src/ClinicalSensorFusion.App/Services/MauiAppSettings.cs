using ClinicalSensorFusion.Core.Settings;

namespace ClinicalSensorFusion.App.Services;

/// <summary>Impostazioni su <see cref="Preferences"/> (NSUserDefaults) con le stesse chiavi dell'app originale.</summary>
public sealed class MauiAppSettings(IPreferences preferences) : IAppSettings
{
    private const string RecordLocationKey = "capture.location";
    private const string DepthOverlayKey = "capture.depth_overlay";
    private const string EnvironmentSensorsKey = "detail.environment_sensors";
    private const string DataQualityKey = "detail.data_quality";
    private const string UseLidarKey = "capture.use_lidar";
    private const string SetupCompletedKey = "setup.completed";
    private const string RequireAuthKey = "security.require_auth";
    private const string SyncServerKey = "sync.server";
    private const string AutoSyncKey = "sync.auto";
    private const string FallRiskKey = "fallrisk.show";

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

    public string OllamaPrompt
    {
        get => preferences.Get(SettingsKeys.OllamaPrompt, string.Empty);
        set => preferences.Set(SettingsKeys.OllamaPrompt, value?.Trim() ?? string.Empty);
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

    /// <summary>Stima sperimentale del rischio di caduta nel dettaglio dei test. Spenta finché non la si sceglie.</summary>
    public bool ShowFallRiskEstimate
    {
        get => preferences.Get(FallRiskKey, false);
        set => preferences.Set(FallRiskKey, value);
    }

    /// <summary>Invio automatico al termine di ogni rilevazione.</summary>
    public bool AutoSync
    {
        get => preferences.Get(AutoSyncKey, true);
        set => preferences.Set(AutoSyncKey, value);
    }

    /// <summary>Profondità LiDAR in modalità corpo. Spenta per impostazione predefinita: si accende quando serve.</summary>
    public bool UseLidar
    {
        get => preferences.Get(UseLidarKey, false);
        set => preferences.Set(UseLidarKey, value);
    }

    /// <summary>Chiede Face ID (o il codice del telefono) all'avvio e al ritorno dal background.</summary>
    public bool RequireDeviceAuthentication
    {
        get => preferences.Get(RequireAuthKey, false);
        set => preferences.Set(RequireAuthKey, value);
    }

    /// <summary>La pagina iniziale di scelta della lingua è già stata completata (o non serviva).</summary>
    public bool SetupCompleted
    {
        get => preferences.Get(SetupCompletedKey, false);
        set => preferences.Set(SetupCompletedKey, value);
    }

    public bool ShowDepthOverlay
    {
        get => preferences.Get(DepthOverlayKey, true);
        set => preferences.Set(DepthOverlayKey, value);
    }

    /// <summary>Nel dettaglio della rilevazione, la sezione dei sensori ambientali è aperta.</summary>
    public bool ShowEnvironmentSensors
    {
        get => preferences.Get(EnvironmentSensorsKey, true);
        set => preferences.Set(EnvironmentSensorsKey, value);
    }

    /// <summary>Nel dettaglio della rilevazione, la sezione «Qualità del dato» è aperta.</summary>
    public bool ShowDataQuality
    {
        get => preferences.Get(DataQualityKey, true);
        set => preferences.Set(DataQualityKey, value);
    }
}
