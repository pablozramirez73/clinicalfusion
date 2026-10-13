using System.Security.Cryptography;
using System.Text;

namespace ClinicalSensorFusion.Core.Telemetry;

/// <summary>
/// Cosa può entrare in un log. I log escono dal perimetro cifrato del database (file diagnostici, console del
/// server, raccoglitori esterni), quindi non devono contenere dati che identificano un paziente.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Sì</b>: identificativi casuali di rilevazioni e registrazioni, tipi di flusso, conteggi, durate, esiti.</item>
/// <item><b>Mai</b>: codice soggetto, nomi, note, testo delle analisi, contenuto dei prompt, chiavi e password.</item>
/// <item>Se serve correlare le righe di uno stesso soggetto, si usa <see cref="Pseudonym"/>.</item>
/// </list>
/// </remarks>
public static class LogRedaction
{
    /// <summary>
    /// Riferimento stabile e non reversibile a un valore sensibile (es. il codice soggetto): le stesse righe si
    /// riconoscono tra loro, ma dal log non si risale al valore.
    /// </summary>
    public static string Pseudonym(string? value) =>
        string.IsNullOrEmpty(value)
            ? "-"
            : "p_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 5).ToLowerInvariant();

    /// <summary>Toglie ritorni a capo e caratteri di controllo da un testo che arriva dall'esterno (anti log injection) e lo accorcia.</summary>
    public static string Sanitize(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (text.Length >= maxLength)
            {
                text.Append('…');
                break;
            }

            text.Append(char.IsControl(c) ? ' ' : c);
        }

        return text.ToString();
    }
}
