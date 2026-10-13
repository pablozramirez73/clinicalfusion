using ClinicalSensorFusion.Core.Localization;
using System.Net;
using System.Net.Sockets;
using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.App.Services;

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
}

/// <summary>
/// Rifiuta le richieste HTTP in chiaro verso Internet. Su iOS lo fa il sistema (App Transport Security con
/// <c>NSAllowsLocalNetworking</c>); su Android la regola di sistema non distingue la rete locale, quindi il
/// controllo sta qui, su ogni richiesta dei client dell'app.
/// </summary>
public sealed class LocalCleartextHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && uri.Scheme == Uri.UriSchemeHttp && !LocalNetwork.IsLocalHost(uri.Host))
        {
            throw new HttpRequestException(Loc.F("Connessione non cifrata rifiutata verso {0}: fuori dalla rete locale serve HTTPS.", uri.Host));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
