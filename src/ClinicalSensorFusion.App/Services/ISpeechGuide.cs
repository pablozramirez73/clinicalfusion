using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.App.Services;

/// <summary>Voce che legge al paziente le istruzioni di un test (sintesi vocale del sistema).</summary>
public interface ISpeechGuide
{
    /// <summary>Pronuncia il testo e termina quando la frase è finita (o all'annullamento).</summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sintesi vocale del sistema nella lingua dell'interfaccia (<see cref="Loc.Language"/>): le guide arrivano già
/// tradotte dal catalogo dei test. Nessun file audio nel pacchetto: i testi si possono correggere senza rifare
/// registrazioni. Se il telefono non ha una voce in quella lingua si usa quella predefinita del sistema.
/// </summary>
internal sealed class TextToSpeechGuide(ITextToSpeech textToSpeech) : ISpeechGuide
{
    private Locale? _locale;
    private bool _localeResolved;

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_localeResolved)
        {
            var locales = await textToSpeech.GetLocalesAsync();
            _locale = locales.FirstOrDefault(l => l.Language.StartsWith(Loc.Language, StringComparison.OrdinalIgnoreCase));
            _localeResolved = true;
        }

        await textToSpeech.SpeakAsync(text, new SpeechOptions { Locale = _locale }, cancellationToken);
    }
}
