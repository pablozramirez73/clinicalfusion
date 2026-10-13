using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace ClinicalSensorFusion.Core.Localization;

/// <summary>
/// Traduzione dei testi mostrati all'utente. La chiave è il testo italiano così com'è scritto nel codice
/// (stile gettext): il codice resta leggibile, l'italiano non ha bisogno di un file e una traduzione
/// mancante ricade sull'italiano invece di mostrare una sigla.
/// </summary>
/// <remarks>
/// Le traduzioni sono file JSON incorporati (<c>Localization/&lt;lingua&gt;.json</c>, oggetto
/// <c>{ "testo italiano": "traduzione" }</c>). Per aggiungere una lingua basta un nuovo file e una voce in
/// <see cref="Supported"/>. I testi con parametri usano i segnaposto di <see cref="string.Format(string, object?[])"/>
/// (<c>"Inviate {0} rilevazioni"</c>) e passano da <see cref="F"/>.
/// Non si traducono i dati: codici, nomi salvati nei riepiloghi, messaggi di log.
/// </remarks>
public static class Loc
{
    public const string Italian = "it";

    /// <summary>Lingue disponibili: codice ISO a due lettere e nome nella lingua stessa.</summary>
    public static IReadOnlyList<(string Code, string Name)> Supported { get; } = [(Italian, "Italiano"), ("en", "English")];

    private static readonly Dictionary<string, FrozenDictionary<string, string>> Catalogs = [];
    private static readonly Lock Gate = new();

    // Catalogo della lingua corrente, letto senza lock: T() si chiama anche a ogni fotogramma (didascalie
    // dell'overlay). Lingua e voci stanno nello stesso oggetto, così un cambio di lingua non le scompagina.
    private static volatile CurrentCatalog? _current;

    /// <summary>Lingua corrente dell'interfaccia (codice a due lettere).</summary>
    public static string Language { get; private set; } = Italian;

    /// <summary>Lingua da usare per una cultura: la sua se è disponibile, altrimenti l'inglese.</summary>
    public static string LanguageFor(CultureInfo culture)
    {
        var code = culture.TwoLetterISOLanguageName;
        return Supported.Any(l => l.Code == code) ? code : "en";
    }

    /// <summary>Imposta la lingua dell'interfaccia; un codice non disponibile equivale all'inglese.</summary>
    public static void Use(string languageCode)
    {
        Language = Supported.Any(l => l.Code == languageCode) ? languageCode : "en";
        _current = new CurrentCatalog(Language, Catalog(Language));
    }

    /// <summary>
    /// Lingua dell'interfaccia per una preferenza dell'utente: la lingua scelta se è disponibile, altrimenti
    /// (preferenza vuota, cioè «come il telefono», o non più disponibile) quella di <paramref name="system"/>.
    /// </summary>
    public static string Resolve(string? preference, CultureInfo system) =>
        Supported.Any(l => l.Code == preference) ? preference! : LanguageFor(system);

    /// <summary>Traduce nella lingua corrente.</summary>
    public static string T(string italian) => Translate(italian, Language);

    /// <summary>Traduce nella lingua di <paramref name="culture"/> (per chi produce testo per un lettore preciso, come i rapporti).</summary>
    public static string T(string italian, CultureInfo culture) => Translate(italian, LanguageFor(culture));

    /// <summary>Traduce un testo con segnaposto e lo completa con i valori, formattati secondo la cultura corrente.</summary>
    public static string F(string italianFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(italianFormat), args);

    /// <summary>Vero se <paramref name="italian"/> ha una traduzione in <paramref name="languageCode"/> (per i test di completezza).</summary>
    public static bool Has(string italian, string languageCode) => languageCode == Italian || Catalog(languageCode).ContainsKey(italian);

    private static string Translate(string italian, string language)
    {
        if (language == Italian || string.IsNullOrEmpty(italian))
        {
            return italian;
        }

        var catalog = _current is { } current && current.Language == language ? current.Entries : Catalog(language);
        return catalog.TryGetValue(italian, out var translated) ? translated : italian;
    }

    private static FrozenDictionary<string, string> Catalog(string language)
    {
        lock (Gate)
        {
            if (Catalogs.TryGetValue(language, out var catalog))
            {
                return catalog;
            }

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"ClinicalSensorFusion.Core.Localization.{language}.json");
            if (stream is not null)
            {
                // JsonDocument e non la deserializzazione per riflessione: il Core è compilato AOT e trimmato.
                using var document = JsonDocument.Parse(stream);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.GetString() is { Length: > 0 } value)
                    {
                        entries[property.Name] = value;
                    }
                }
            }

            return Catalogs[language] = entries.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    private sealed record CurrentCatalog(string Language, FrozenDictionary<string, string> Entries);
}
