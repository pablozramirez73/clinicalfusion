namespace ClinicalSensorFusion.App.Services;

/// <summary>Voce che legge al paziente le istruzioni di un test (sintesi vocale del sistema).</summary>
public interface ISpeechGuide
{
    /// <summary>Pronuncia il testo e termina quando la frase è finita (o all'annullamento).</summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sintesi vocale di iOS in italiano. Nessun file audio nel pacchetto: i testi delle guide stanno nel
/// catalogo dei test e si possono correggere senza rifare registrazioni.
/// </summary>
internal sealed class TextToSpeechGuide(ITextToSpeech textToSpeech) : ISpeechGuide
{
    private Locale? _italian;
    private bool _localeResolved;

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_localeResolved)
        {
            var locales = await textToSpeech.GetLocalesAsync();
            _italian = locales.FirstOrDefault(l => l.Language.StartsWith("it", StringComparison.OrdinalIgnoreCase));
            _localeResolved = true;
        }

        await textToSpeech.SpeakAsync(text, new SpeechOptions { Locale = _italian }, cancellationToken);
    }
}
