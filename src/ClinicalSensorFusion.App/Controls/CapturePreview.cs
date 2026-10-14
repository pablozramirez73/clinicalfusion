namespace ClinicalSensorFusion.App.Controls;

/// <summary>
/// Anteprima della camera con mappa di profondità. Vista "vuota" lato MAUI: il rendering è nell'handler
/// di piattaforma (iOS: MTKView + ArPreviewRenderer).
/// </summary>
public sealed class CapturePreview : View
{
    public static readonly BindableProperty DepthOverlayOpacityProperty = BindableProperty.Create(
        nameof(DepthOverlayOpacity), typeof(double), typeof(CapturePreview), 0.35);

    public double DepthOverlayOpacity
    {
        get => (double)GetValue(DepthOverlayOpacityProperty);
        set => SetValue(DepthOverlayOpacityProperty, value);
    }
}
