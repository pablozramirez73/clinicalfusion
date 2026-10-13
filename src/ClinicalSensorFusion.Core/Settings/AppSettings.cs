namespace ClinicalSensorFusion.Core.Settings;

/// <summary>
/// Chiavi delle preferenze. Sono le stesse trovate nel contenitore dell'app originale
/// (<c>ollama.server</c>, <c>ollama.model</c>, <c>ollama.language</c>, <c>operator.id</c>, <c>operator.name</c>):
/// non rinominarle, per compatibilità con eventuali backup delle preferenze.
/// </summary>
public static class SettingsKeys
{
    public const string OllamaServer = "ollama.server";
    public const string OllamaModel = "ollama.model";
    public const string OllamaLanguage = "ollama.language";

    /// <summary>Istruzioni aggiuntive dell'operatore per il modello linguistico.</summary>
    public const string OllamaPrompt = "ollama.prompt";
    public const string OperatorId = "operator.id";
    public const string OperatorName = "operator.name";

    /// <summary>
    /// Lingua dell'interfaccia (codice di <see cref="Localization.Loc.Supported"/>; vuoto = quella del telefono).
    /// Diversa da <see cref="OllamaLanguage"/>, che è la lingua in cui risponde il modello.
    /// </summary>
    public const string UiLanguage = "ui.language";

    /// <summary>Credenziali di servizio Cloudflare Access: solo nel Portachiavi, mai nelle preferenze in chiaro.</summary>
    public const string CloudflareClientId = "cloudflare.access.client_id";
    public const string CloudflareClientSecret = "cloudflare.access.client_secret";
}

public static class SettingsDefaults
{
    public const string OllamaModel = "gemma4:e4b-mlx";
    public const string OllamaLanguage = "Italiano";
    public const int OllamaPort = 11434;

    public static readonly string[] Languages = ["Italiano", "English"];
}

/// <summary>Impostazioni dell'app. Implementata su iOS con <c>Preferences</c> (NSUserDefaults) e <c>SecureStorage</c> (Portachiavi).</summary>
public interface IAppSettings
{
    string OllamaServer { get; set; }

    string OllamaModel { get; set; }

    string OllamaLanguage { get; set; }

    /// <summary>Istruzioni aggiuntive per il modello (vuoto = solo quelle predefinite).</summary>
    string OllamaPrompt { get; set; }

    string OperatorId { get; set; }

    string OperatorName { get; set; }

    bool IsOperatorConfigured => !string.IsNullOrWhiteSpace(OperatorId) && !string.IsNullOrWhiteSpace(OperatorName);
}
