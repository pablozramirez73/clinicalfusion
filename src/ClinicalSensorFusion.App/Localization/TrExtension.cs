using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.App.Localization;

/// <summary>
/// Testo tradotto in XAML: <c>Text="{l:Tr Text='Salva'}"</c>. La chiave è il testo italiano (vedi <see cref="Loc"/>).
/// La lingua si decide all'avvio dell'app: cambiarla dalle Impostazioni ha effetto al riavvio.
/// </summary>
[ContentProperty(nameof(Text))]
[AcceptEmptyServiceProvider]
public sealed class TrExtension : IMarkupExtension<string>
{
    public string Text { get; set; } = string.Empty;

    public string ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
