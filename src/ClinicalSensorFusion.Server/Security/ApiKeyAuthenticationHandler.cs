using ClinicalSensorFusion.Core.Localization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ClinicalSensorFusion.Core.Sync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ClinicalSensorFusion.Server.Security;

/// <summary>
/// Autenticazione dei dispositivi con header <c>X-Api-Key</c>. La chiave ricevuta viene confrontata come hash
/// SHA-256 con <see cref="CryptographicOperations.FixedTimeEquals"/> per non esporre informazioni sui tempi.
/// </summary>
/// <remarks>
/// SHA-256 (e non PBKDF2) è adeguato perché le chiavi sono casuali a 256 bit generate dal server, non password
/// scelte da persone: un attacco a dizionario non ha senso e la verifica resta economica per ogni richiesta.
/// </remarks>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<IngestOptions> ingest) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ClinicalSyncClient.ApiKeyHeader, out var values) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
        foreach (var device in ingest.CurrentValue.Devices)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(device.KeySha256);
            }
            catch (FormatException)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, device.Name), new Claim(ClaimTypes.Role, Roles.Device)],
                    SchemeName);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }

        return Task.FromResult(AuthenticateResult.Fail(Loc.T("Chiave del dispositivo non valida.", Ui.Culture)));
    }

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
