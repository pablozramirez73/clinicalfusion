using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.Core.Tests;

public sealed partial class LocalizationTests
{
    [Theory]
    [InlineData("en", "it-IT", "en")]
    [InlineData("it", "en-US", "it")]
    [InlineData("", "it-IT", "it")] // «come il telefono»
    [InlineData("", "en-GB", "en")]
    [InlineData("", "fr-FR", "en")] // lingua del telefono non disponibile: inglese
    [InlineData(null, "it-IT", "it")]
    [InlineData("de", "it-IT", "it")] // lingua salvata non più disponibile: si torna a quella del telefono
    public void Resolve_prefers_the_saved_language_then_the_phone_one(string? preference, string system, string expected) =>
        Assert.Equal(expected, Loc.Resolve(preference, CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void Use_switches_the_language_of_T_and_F()
    {
        try
        {
            Loc.Use("en");
            Assert.Equal("en", Loc.Language);
            Assert.Equal("Save", Loc.T("Salva"));
            Assert.Equal("Connecting to x…", Loc.F("Connessione a {0}…", "x"));

            // Un testo senza traduzione ricade sull'italiano invece di sparire.
            Assert.Equal("Testo senza traduzione", Loc.T("Testo senza traduzione"));

            Loc.Use(Loc.Italian);
            Assert.Equal("Salva", Loc.T("Salva"));
        }
        finally
        {
            Loc.Use(Loc.Italian);
        }
    }

    /// <summary>
    /// Ogni testo che l'app passa a <c>{l:Tr}</c>, <c>Loc.T</c> o <c>Loc.F</c> deve avere la traduzione in ogni
    /// lingua disponibile: altrimenti chi usa l'app in inglese si ritrova frasi in italiano senza che nessuno se ne accorga.
    /// </summary>
    [Fact]
    public void Every_interface_text_is_translated_in_every_language()
    {
        var keys = InterfaceTexts();
        Assert.NotEmpty(keys);
        foreach (var (code, _) in Loc.Supported.Where(l => l.Code != Loc.Italian))
        {
            var missing = keys.Keys.Where(k => !Loc.Has(k, code)).Select(k => $"{k}  ({keys[k]})").ToList();
            Assert.True(missing.Count == 0, $"Testi senza traduzione in «{code}»:\n" + string.Join("\n", missing));
        }
    }

    /// <summary>Le voci che il codice non usa più vanno tolte, e la traduzione deve avere gli stessi segnaposto dell'originale.</summary>
    [Fact]
    public void Catalogs_have_no_stale_entries_and_keep_the_placeholders()
    {
        var keys = InterfaceTexts();
        foreach (var (code, _) in Loc.Supported.Where(l => l.Code != Loc.Italian))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "ClinicalSensorFusion.Core", "Localization", code + ".json")));
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                Assert.True(keys.ContainsKey(entry.Name), $"«{code}.json»: voce non più usata dal codice: {entry.Name}");
                Assert.Equal(Placeholders(entry.Name), Placeholders(entry.Value.GetString() ?? string.Empty));
            }
        }
    }

    /// <summary>Testi italiani dell'interfaccia, letti dai sorgenti dell'app, con il file dove compaiono la prima volta.</summary>
    private static Dictionary<string, string> InterfaceTexts()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            {
                continue;
            }

            IEnumerable<string> found = Path.GetExtension(file) switch
            {
                ".xaml" => XamlText().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value.Replace("\\'", "'", StringComparison.Ordinal)),
                ".cs" => CodeText().Matches(File.ReadAllText(file))
                    .Select(m => string.Concat(StringLiteral().Matches(m.Groups[1].Value).Select(l => Regex.Unescape(l.Groups[1].Value)))),
                _ => Enumerable.Empty<string>(),
            };
            foreach (var text in found)
            {
                texts.TryAdd(text, relative);
            }
        }

        return texts;
    }

    private static string[] Placeholders(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal)];

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClinicalSensorFusion.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Radice del repository non trovata.");
    }

    // {l:Tr Text='…'}, con \' per l'apostrofo.
    [GeneratedRegex(@"\{l:Tr Text='((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex XamlText();

    // Loc.T("…") e Loc.F("…", …), anche con il testo spezzato in più letterali uniti da +.
    [GeneratedRegex(@"Loc\.[TFN]\(\s*((?:""(?:[^""\\]|\\.)*""\s*\+?\s*)+)[,)]")]
    private static partial Regex CodeText();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex Placeholder();
}
