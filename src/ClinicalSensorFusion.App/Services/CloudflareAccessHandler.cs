using ClinicalSensorFusion.Core.Settings;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Aggiunge le credenziali di servizio Cloudflare Access (se configurate) alle richieste verso Ollama.
/// </summary>
/// <remarks>
/// L'app originale accedeva al server remoto via login nella WebView (cookie <c>CF_Authorization</c>).
/// Quel percorso resta disponibile (pagina "Accesso remoto"), ma il cookie scade; un service token
/// (Client ID + Secret) è la soluzione robusta per un client non interattivo. Le credenziali stanno
/// nel Portachiavi (SecureStorage) e vengono lette a ogni richiesta, così un cambio è immediato.
/// </remarks>
public sealed class CloudflareAccessHandler(ISecureStorage secureStorage) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Scheme == Uri.UriSchemeHttps)
        {
            var id = await secureStorage.GetAsync(SettingsKeys.CloudflareClientId).ConfigureAwait(false);
            var secret = await secureStorage.GetAsync(SettingsKeys.CloudflareClientSecret).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
            {
                request.Headers.TryAddWithoutValidation("CF-Access-Client-Id", id);
                request.Headers.TryAddWithoutValidation("CF-Access-Client-Secret", secret);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
