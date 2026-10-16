namespace ClinicalSensorFusion.Core.Security;

/// <summary>Intestazioni segrete e l'unica origine (schema, host, porta) a cui si possono mandare.</summary>
public sealed record ScopedCredentials(Uri Origin, IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    /// <summary>Stessa origine: schema, host e porta identici. Il percorso non conta.</summary>
    public bool Covers(Uri requestUri) => Origins.Same(requestUri, Origin);
}

/// <summary>
/// Aggiunge le credenziali (chiave del dispositivo, token di Cloudflare) solo alle richieste verso l'origine
/// configurata. Va usato con i reindirizzamenti disattivati nel gestore di rete: un reindirizzamento seguito
/// in automatico ripeterebbe le intestazioni verso un altro host senza passare di qui, perché .NET toglie
/// nei reindirizzamenti solo l'intestazione Authorization, non quelle personalizzate.
/// </summary>
public sealed class ScopedCredentialsHandler(Func<CancellationToken, Task<ScopedCredentials?>> credentials) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && await credentials(cancellationToken).ConfigureAwait(false) is { } scoped && scoped.Covers(uri))
        {
            foreach (var (name, value) in scoped.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
