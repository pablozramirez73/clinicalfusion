using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.App.Localization;

/// <summary>
/// Testo tradotto in XAML: <c>Text="{l:Tr Text='Salva'}"</c>. La chiave è il testo italiano (vedi <see cref="Loc"/>).
/// Il testo si traduce una volta sola, quando la pagina viene creata: per questo la lingua si fissa all'avvio
/// (<c>Loc.Use</c> in <see cref="MauiProgram"/>, dalla preferenza «Lingua dell'app» o dalla lingua del telefono)
/// e cambiarla dalle Impostazioni ha effetto al riavvio.
/// </summary>
[ContentProperty(nameof(Text))]
[AcceptEmptyServiceProvider]
public sealed class TrExtension : IMarkupExtension<string>
{
    public string Text { get; set; } = string.Empty;

    public string ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
