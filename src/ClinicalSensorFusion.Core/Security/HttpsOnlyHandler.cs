using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.Core.Security;

/// <summary>
/// Rifiuta ogni richiesta che non sia HTTPS, anche in rete locale: dati clinici, chiave del dispositivo e
/// prompt al modello viaggiano sempre cifrati. Il server di sviluppo in rete locale usa un certificato
/// autofirmato riconosciuto dall'impronta (<see cref="CertificatePin"/>); Ollama, che parla solo HTTP, si
/// raggiunge attraverso il server clinico o un proxy HTTPS.
/// </summary>
public sealed class HttpsOnlyHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException(Loc.F("Connessione non cifrata rifiutata verso {0}: serve HTTPS.", uri.Host));
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Confronto di origini (schema, host, porta), come lo intende il web.</summary>
public static class Origins
{
    public static bool Same(Uri a, Uri b) =>
        a.IsAbsoluteUri && b.IsAbsoluteUri
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
