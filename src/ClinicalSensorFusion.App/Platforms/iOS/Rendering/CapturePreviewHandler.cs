using ClinicalSensorFusion.App.Controls;
using ClinicalSensorFusion.App.Platforms.iOS.Services;
using ClinicalSensorFusion.App.Services;
using Metal;
using Microsoft.Extensions.DependencyInjection;
using MetalKit;
using Microsoft.Maui.Handlers;

namespace ClinicalSensorFusion.App.Platforms.iOS.Rendering;

/// <summary>Collega <see cref="CapturePreview"/> a una MTKView disegnata da <see cref="ArPreviewRenderer"/>.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Il renderer è rilasciato in DisconnectHandler, il punto di fine vita degli handler MAUI.")]
internal sealed class CapturePreviewHandler : ViewHandler<CapturePreview, MTKView>
{
    public static readonly IPropertyMapper<CapturePreview, CapturePreviewHandler> Mapper =
        new PropertyMapper<CapturePreview, CapturePreviewHandler>(ViewMapper)
        {
            [nameof(CapturePreview.DepthOverlayOpacity)] = MapDepthOverlayOpacity,
        };

    private ArPreviewRenderer? _renderer;

    public CapturePreviewHandler() : base(Mapper)
    {
    }

    private IosCaptureEngine Engine =>
        (IosCaptureEngine)(MauiContext ?? throw new InvalidOperationException("Handler non connesso.")).Services.GetRequiredService<ICaptureEngine>();

    protected override MTKView CreatePlatformView()
    {
        var device = MTLDevice.SystemDefault ?? throw new NotSupportedException("Metal non disponibile.");
        _renderer = new ArPreviewRenderer(device, Engine.Ar);
        return new MTKView(CoreGraphics.CGRect.Empty, device)
        {
            ColorPixelFormat = MTLPixelFormat.BGRA8Unorm,
            FramebufferOnly = true,
            PreferredFramesPerSecond = 60,
            Delegate = _renderer,
            AutoResizeDrawable = true,
        };
    }

    protected override void DisconnectHandler(MTKView platformView)
    {
        platformView.Delegate = null!;
        platformView.Paused = true;
        _renderer?.Dispose();
        _renderer = null;
        base.DisconnectHandler(platformView);
    }

    private static void MapDepthOverlayOpacity(CapturePreviewHandler handler, CapturePreview view) =>
        handler.Engine.Ar.DepthOverlayOpacity = (float)Math.Clamp(view.DepthOverlayOpacity, 0, 1);
}
