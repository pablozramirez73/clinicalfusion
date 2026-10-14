using AndroidX.Camera.View;
using ClinicalSensorFusion.App.Controls;
using ClinicalSensorFusion.App.Platforms.Android.Services;
using ClinicalSensorFusion.App.Services;
using Microsoft.Maui.Handlers;

namespace ClinicalSensorFusion.App.Platforms.Android.Rendering;

/// <summary>Collega <see cref="CapturePreview"/> alla <see cref="PreviewView"/> di CameraX.</summary>
internal sealed class CapturePreviewHandler() : ViewHandler<CapturePreview, PreviewView>(Mapper)
{
    // DepthOverlayOpacity non ha effetto: su Android non c'è una mappa di profondità da sovrapporre.
    public static readonly IPropertyMapper<CapturePreview, CapturePreviewHandler> Mapper =
        new PropertyMapper<CapturePreview, CapturePreviewHandler>(ViewMapper);

    private AndroidCaptureEngine Engine =>
        (AndroidCaptureEngine)(MauiContext ?? throw new InvalidOperationException("Handler non connesso.")).Services.GetRequiredService<ICaptureEngine>();

    protected override PreviewView CreatePlatformView()
    {
        var view = new PreviewView(Context);
        // TextureView e non SurfaceView: si compone con le altre viste MAUI (overlay, pannelli, cambio di scheda).
        view.SetImplementationMode(PreviewView.ImplementationMode.Compatible!);
        view.SetScaleType(PreviewView.ScaleType.FillCenter!);
        return view;
    }

    protected override void ConnectHandler(PreviewView platformView)
    {
        base.ConnectHandler(platformView);
        Engine.Camera.AttachPreview(platformView);
    }

    protected override void DisconnectHandler(PreviewView platformView)
    {
        Engine.Camera.AttachPreview(null);
        base.DisconnectHandler(platformView);
    }
}
