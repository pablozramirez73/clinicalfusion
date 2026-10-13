using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace ClinicalSensorFusion.Server;

/// <summary>
/// Lingua dei testi che il server restituisce a chi lo chiama (messaggi d'errore, rapporto della rilevazione,
/// descrizioni). La sceglie il client con l'intestazione <c>Accept-Language</c>: la dashboard manda la lingua
/// scelta dall'utente. Senza intestazione resta l'italiano, come prima che i testi fossero traducibili.
/// </summary>
internal static class Ui
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>Lingua della richiesta in corso (vedi <see cref="UseRequestLanguage"/>).</summary>
    public static CultureInfo Culture => CultureInfo.CurrentUICulture;

    /// <summary>
    /// Imposta la lingua dei testi per ogni richiesta. Tocca solo la cultura dell'interfaccia: numeri e date
    /// delle risposte JSON restano nel formato invariante.
    /// </summary>
    public static IApplicationBuilder UseRequestLanguage(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        CultureInfo.CurrentUICulture = From(context.Request.Headers.AcceptLanguage.ToString());
        return next(context);
    });

    /// <summary>La lingua preferita tra quelle disponibili (italiano, inglese); italiano se non ce n'è nessuna.</summary>
    public static CultureInfo From(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage) || !StringWithQualityHeaderValue.TryParseList([acceptLanguage], out var preferences))
        {
            return Italian;
        }

        foreach (var preference in preferences.OrderByDescending(p => p.Quality ?? 1))
        {
            var tag = preference.Value.Value ?? string.Empty;
            if (tag.StartsWith("it", StringComparison.OrdinalIgnoreCase))
            {
                return Italian;
            }

            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return English;
            }
        }

        return Italian;
    }
}
