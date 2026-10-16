using System.Net;
using System.Net.Sockets;
using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.Core.Security;

/// <summary>Riconosce gli indirizzi della rete locale, gli unici verso cui è ammesso HTTP in chiaro.</summary>
public static class LocalNetwork
{
    public static bool IsLocalHost(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
            }

            return ip.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..] or [169, 254, ..];
        }

        // Nomi senza dominio ("mac-studio") o mDNS ("mac-studio.local"): si risolvono solo in rete locale.
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>Vero se l'indirizzo è HTTPS, oppure HTTP verso la rete locale.</summary>
    public static bool IsAllowedTransport(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLocalHost(uri.Host));
}

/// <summary>
/// Rifiuta le richieste HTTP in chiaro verso Internet, su ogni piattaforma. Su iOS c'è anche App Transport
/// Security, ma non copre gli indirizzi IP scritti per esteso: senza questo controllo un «http://1.2.3.4»
/// manderebbe dati clinici e chiave del dispositivo in chiaro su Internet.
/// </summary>
public sealed class LocalCleartextHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && !LocalNetwork.IsAllowedTransport(uri))
        {
            throw new HttpRequestException(Loc.F("Connessione non cifrata rifiutata verso {0}: fuori dalla rete locale serve HTTPS.", uri.Host));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
