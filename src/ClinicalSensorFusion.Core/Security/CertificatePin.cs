using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClinicalSensorFusion.Core.Security;

/// <summary>Il server clinico e l'impronta della chiave pubblica che il suo certificato deve avere.</summary>
public sealed record PinnedServer(Uri Origin, string Pin);

/// <summary>
/// Certificate pinning: verso il server configurato vale l'impronta SHA-256 della chiave pubblica
/// (SubjectPublicKeyInfo, in base64, come in HPKP e in <c>openssl … | openssl dgst -sha256 -binary | base64</c>).
/// Così il server in rete locale può usare un certificato autofirmato senza che l'app accetti certificati
/// qualunque: chi si mette in mezzo non ha quella chiave. Verso tutti gli altri host, o senza impronta
/// configurata, vale la normale verifica del sistema.
/// </summary>
public static class CertificatePin
{
    public static string Compute(X509Certificate2 certificate) =>
        Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>Forma canonica dell'impronta scritta dall'operatore: senza spazi né prefisso «sha256/».</summary>
    public static string? Normalize(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return null;
        }

        var text = string.Concat(pin.Where(c => !char.IsWhiteSpace(c)));
        return text.StartsWith("sha256/", StringComparison.OrdinalIgnoreCase) ? text["sha256/".Length..] : text;
    }

    /// <summary>Vero se l'impronta è un SHA-256 in base64 (32 byte).</summary>
    public static bool IsWellFormed(string? pin)
    {
        var normalized = Normalize(pin);
        var buffer = new byte[48];
        return normalized is not null && Convert.TryFromBase64String(normalized, buffer, out var written) && written == 32;
    }

    /// <summary>Decisione per il callback di validazione del certificato dei client HTTP dell'app.</summary>
    public static bool Validate(Uri? requestUri, X509Certificate2? certificate, SslPolicyErrors errors, PinnedServer? pinned)
    {
        if (pinned is not null && requestUri is not null && Origins.Same(requestUri, pinned.Origin))
        {
            // L'impronta sostituisce la catena di fiducia: un certificato autofirmato va bene se la chiave è quella.
            return certificate is not null
                && CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Compute(certificate)),
                    System.Text.Encoding.ASCII.GetBytes(Normalize(pinned.Pin) ?? string.Empty));
        }

        return errors == SslPolicyErrors.None;
    }
}
