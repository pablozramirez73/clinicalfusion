namespace ClinicalSensorFusion.Core.Ai;

/// <summary>Normalizzazione dell'indirizzo del server Ollama inserito dall'operatore.</summary>
public static class OllamaEndpoint
{
    /// <summary>
    /// Accetta "192.168.1.20", "192.168.1.20:11434", "http://host:11434/", "https://ai.example.org".
    /// Senza schema si assume http e, senza porta, 11434 (default Ollama). Con https esplicito senza porta
    /// si lascia la 443 (tipico dietro un reverse proxy come Cloudflare Access).
    /// </summary>
    public static bool TryParse(string? input, out Uri? baseUri)
    {
        baseUri = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        var hasScheme = text.Contains("://", StringComparison.Ordinal);
        if (!hasScheme)
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        var explicitPort = HasExplicitPort(text);
        if (!explicitPort && uri.Scheme == Uri.UriSchemeHttp)
        {
            builder.Port = Settings.SettingsDefaults.OllamaPort;
        }

        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        baseUri = builder.Uri;
        return true;
    }

    private static bool HasExplicitPort(string text)
    {
        var authorityStart = text.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = text.IndexOf('/', authorityStart);
        var authority = authorityEnd < 0 ? text[authorityStart..] : text[authorityStart..authorityEnd];
        // IPv6 tra parentesi quadre: la porta segue "]:".
        var bracket = authority.LastIndexOf(']');
        return authority.IndexOf(':', bracket + 1) >= 0;
    }
}
