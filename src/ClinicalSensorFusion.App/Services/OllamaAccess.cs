using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Security;
using ClinicalSensorFusion.Core.Settings;
using ClinicalSensorFusion.Core.Sync;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Credenziali per le richieste verso Ollama. Due casi, anche insieme:
/// <list type="bullet">
/// <item>Ollama attraverso il server clinico (<c>https://server/api/v1/ai/</c>): stessa origine del server, quindi
/// parte la chiave del dispositivo. È il modo di usare un Ollama in rete locale senza traffico in chiaro.</item>
/// <item>Ollama dietro Cloudflare Access: il service token (Client ID + Secret) dal Portachiavi.</item>
/// </list>
/// </summary>
/// <remarks>
/// L'app originale accedeva al server remoto via login nella WebView (cookie <c>CF_Authorization</c>).
/// Quel percorso resta disponibile (pagina "Accesso remoto"), ma il cookie scade; un service token è la
/// soluzione robusta per un client non interattivo. Le credenziali vengono lette a ogni richiesta, così un
/// cambio è immediato, e partono solo verso il server Ollama configurato (<see cref="ScopedCredentialsHandler"/>).
/// </remarks>
public static class OllamaAccess
{
    public static async Task<ScopedCredentials?> CredentialsAsync(MauiAppSettings settings, ISecureStorage secureStorage)
    {
        if (!OllamaEndpoint.TryParse(settings.OllamaServer, out var server) || server!.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var headers = new List<KeyValuePair<string, string>>();
        if (SyncService.TryGetServer(settings, out var clinical) && Origins.Same(server, clinical!)
            && await secureStorage.GetAsync(SyncService.ApiKeyStorageKey).ConfigureAwait(false) is { Length: > 0 } key)
        {
            headers.Add(new(ClinicalSyncClient.ApiKeyHeader, key));
        }

        var id = await secureStorage.GetAsync(SettingsKeys.CloudflareClientId).ConfigureAwait(false);
        var secret = await secureStorage.GetAsync(SettingsKeys.CloudflareClientSecret).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
        {
            headers.Add(new("CF-Access-Client-Id", id));
            headers.Add(new("CF-Access-Client-Secret", secret));
        }

        return headers.Count == 0 ? null : new ScopedCredentials(server, headers);
    }
}
