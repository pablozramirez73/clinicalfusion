using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Security;
using ClinicalSensorFusion.Core.Settings;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Credenziali di servizio Cloudflare Access (se configurate) per le richieste verso Ollama.
/// </summary>
/// <remarks>
/// L'app originale accedeva al server remoto via login nella WebView (cookie <c>CF_Authorization</c>).
/// Quel percorso resta disponibile (pagina "Accesso remoto"), ma il cookie scade; un service token
/// (Client ID + Secret) è la soluzione robusta per un client non interattivo. Le credenziali stanno
/// nel Portachiavi (SecureStorage) e vengono lette a ogni richiesta, così un cambio è immediato; partono
/// solo verso il server Ollama configurato e solo in HTTPS (<see cref="ScopedCredentialsHandler"/>).
/// </remarks>
public static class CloudflareAccess
{
    public static async Task<ScopedCredentials?> CredentialsAsync(MauiAppSettings settings, ISecureStorage secureStorage)
    {
        if (!OllamaEndpoint.TryParse(settings.OllamaServer, out var server) || server!.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var id = await secureStorage.GetAsync(SettingsKeys.CloudflareClientId).ConfigureAwait(false);
        var secret = await secureStorage.GetAsync(SettingsKeys.CloudflareClientSecret).ConfigureAwait(false);
        return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)
            ? null
            : new ScopedCredentials(server, [new("CF-Access-Client-Id", id), new("CF-Access-Client-Secret", secret)]);
    }
}
