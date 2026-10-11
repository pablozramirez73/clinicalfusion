using System.Runtime.InteropServices;
using ARKit;
using ClinicalSensorFusion.App.Platforms.iOS.Vision;
using CoreGraphics;
using CoreVideo;
using Foundation;
using Metal;
using MetalKit;
using UIKit;

namespace ClinicalSensorFusion.App.Platforms.iOS.Rendering;

/// <summary>
/// Anteprima della camera disegnata direttamente con Metal: conversione YCbCr→RGB in shader e
/// sovrapposizione della mappa di profondità LiDAR colorata (colormap "turbo").
/// </summary>
/// <remarks>
/// Perché Metal e non ARSCNView/AVCaptureVideoPreviewLayer: serve fondere nello stesso passaggio immagine
/// e profondità, con la stessa trasformazione di visualizzazione usata per l'overlay dei punti, a 60 fps
/// e senza copie CPU. Le texture vengono create a costo zero dai CVPixelBuffer tramite CVMetalTextureCache.
/// </remarks>
internal sealed class ArPreviewRenderer : NSObject, IMTKViewDelegate
{
    private const string ShaderSource = """
        #include <metal_stdlib>
        using namespace metal;

        struct Uniforms {
            float4 t0;   // a, b, c, d della trasformazione vista→immagine
            float4 t1;   // tx, ty, opacità profondità, profondità massima (m)
        };

        struct VOut {
            float4 position [[position]];
            float2 uv;
        };

        vertex VOut vertex_main(uint vid [[vertex_id]], constant Uniforms& u [[buffer(0)]]) {
            const float2 pos[4] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(1, 1) };
            const float2 view[4] = { float2(0, 1), float2(1, 1), float2(0, 0), float2(1, 0) };
            float2 v = view[vid];
            VOut o;
            o.position = float4(pos[vid], 0, 1);
            o.uv = float2(u.t0.x * v.x + u.t0.z * v.y + u.t1.x,
                          u.t0.y * v.x + u.t0.w * v.y + u.t1.y);
            return o;
        }

        // Approssimazione polinomiale della colormap Turbo (Google): vicino = rosso, lontano = blu.
        static float3 turbo(float x) {
            const float4 kR4 = float4(0.13572138, 4.61539260, -42.66032258, 132.13108234);
            const float4 kG4 = float4(0.09140261, 2.19418839, 4.84296658, -14.18503333);
            const float4 kB4 = float4(0.10667330, 12.64194608, -60.58204836, 110.36276771);
            const float2 kR2 = float2(-152.94239396, 59.28637943);
            const float2 kG2 = float2(4.27729857, 2.82956604);
            const float2 kB2 = float2(-89.90310912, 27.34824973);
            x = saturate(x);
            float4 v4 = float4(1.0, x, x * x, x * x * x);
            float2 v2 = v4.zw * v4.z;
            return float3(dot(v4, kR4) + dot(v2, kR2), dot(v4, kG4) + dot(v2, kG2), dot(v4, kB4) + dot(v2, kB2));
        }

        fragment float4 fragment_main(VOut in [[stage_in]],
                                      texture2d<float> texY [[texture(0)]],
                                      texture2d<float> texCbCr [[texture(1)]],
                                      texture2d<float> texDepth [[texture(2)]],
                                      constant Uniforms& u [[buffer(0)]]) {
            constexpr sampler s(filter::linear, address::clamp_to_edge);
            const float4x4 ycbcrToRgb = float4x4(
                float4(+1.0000, +1.0000, +1.0000, +0.0000),
                float4(+0.0000, -0.3441, +1.7720, +0.0000),
                float4(+1.4020, -0.7141, +0.0000, +0.0000),
                float4(-0.7010, +0.5291, -0.8860, +1.0000));
            float4 ycbcr = float4(texY.sample(s, in.uv).r, texCbCr.sample(s, in.uv).rg, 1.0);
            float4 rgb = ycbcrToRgb * ycbcr;
            if (u.t1.z > 0.0) {
                float d = texDepth.sample(s, in.uv).r;
                if (d > 0.0) {
                    rgb.rgb = mix(rgb.rgb, turbo(1.0 - d / u.t1.w), u.t1.z);
                }
            }
            return float4(rgb.rgb, 1.0);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public float A, B, C, D;
        public float Tx, Ty, DepthOpacity, MaxDepth;
    }

    private readonly ArCaptureController _capture;
    private readonly IMTLDevice _device;
    private readonly IMTLCommandQueue _queue;
    private readonly IMTLRenderPipelineState _pipeline;
    private readonly CVMetalTextureCache _textureCache;
    private readonly IMTLTexture _emptyDepth;

    public ArPreviewRenderer(IMTLDevice device, ArCaptureController capture)
    {
        _device = device;
        _capture = capture;
        _queue = device.CreateCommandQueue() ?? throw new InvalidOperationException("Metal: coda comandi non disponibile.");
        _textureCache = CVMetalTextureCache.FromDevice(device) ?? throw new InvalidOperationException("Metal: texture cache non disponibile.");

        var library = device.CreateLibrary(ShaderSource, new MTLCompileOptions(), out var error)
            ?? throw new InvalidOperationException($"Compilazione shader fallita: {error?.LocalizedDescription}");
        var descriptor = new MTLRenderPipelineDescriptor
        {
            VertexFunction = library.CreateFunction("vertex_main"),
            FragmentFunction = library.CreateFunction("fragment_main"),
        };
        descriptor.ColorAttachments[0].PixelFormat = MTLPixelFormat.BGRA8Unorm;
        _pipeline = device.CreateRenderPipelineState(descriptor, out error)
            ?? throw new InvalidOperationException($"Pipeline Metal non creata: {error?.LocalizedDescription}");

        // Texture 1×1 a profondità 0 per le modalità senza LiDAR (frontale).
        var depthDescriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(MTLPixelFormat.R32Float, 1, 1, false);
        _emptyDepth = device.CreateTexture(depthDescriptor)!;
    }

    public void DrawableSizeWillChange(MTKView view, CGSize size) => _capture.Viewport = view.Bounds.Size;

    public void Draw(MTKView view)
    {
        using var frame = _capture.Session.CurrentFrame;
        if (frame is null || view.CurrentRenderPassDescriptor is not { } pass || view.CurrentDrawable is not { } drawable)
        {
            return;
        }

        var viewport = view.Bounds.Size;
        _capture.Viewport = viewport;

        using var image = frame.CapturedImage;
        if (image.PlaneCount < 2)
        {
            return;
        }

        var y = CreateTexture(image, MTLPixelFormat.R8Unorm, 0);
        var cbcr = CreateTexture(image, MTLPixelFormat.RG8Unorm, 1);
        if (y?.Texture is not { } yTexture || cbcr?.Texture is not { } cbcrTexture)
        {
            y?.Dispose();
            cbcr?.Dispose();
            return;
        }

        CVMetalTexture? depth = null;
        using var depthData = frame.SceneDepth;
        if (_capture.DepthOverlayOpacity > 0 && depthData?.DepthMap is { } depthMap)
        {
            depth = CreateTexture(depthMap, MTLPixelFormat.R32Float, 0);
        }

        // Trasformazione inversa: dalle coordinate della vista a quelle dell'immagine (per campionare le texture).
        var toImage = frame.GetDisplayTransform(UIInterfaceOrientation.Portrait, viewport).Invert();
        var uniforms = new Uniforms
        {
            A = (float)toImage.A,
            B = (float)toImage.B,
            C = (float)toImage.C,
            D = (float)toImage.D,
            Tx = (float)toImage.Tx,
            Ty = (float)toImage.Ty,
            DepthOpacity = depth is null ? 0 : _capture.DepthOverlayOpacity,
            MaxDepth = 5f,
        };

        var commands = _queue.CommandBuffer();
        if (commands is null)
        {
            return;
        }

        var encoder = commands.CreateRenderCommandEncoder(pass);
        encoder.SetRenderPipelineState(_pipeline);
        unsafe
        {
            encoder.SetVertexBytes((IntPtr)(&uniforms), (nuint)sizeof(Uniforms), 0);
            encoder.SetFragmentBytes((IntPtr)(&uniforms), (nuint)sizeof(Uniforms), 0);
        }

        encoder.SetFragmentTexture(yTexture, 0);
        encoder.SetFragmentTexture(cbcrTexture, 1);
        encoder.SetFragmentTexture(depth?.Texture ?? _emptyDepth, 2);
        encoder.DrawPrimitives(MTLPrimitiveType.TriangleStrip, 0, 4);
        encoder.EndEncoding();
        commands.PresentDrawable(drawable);

        // Le CVMetalTexture devono vivere finché la GPU non ha finito di leggerle.
        commands.AddCompletedHandler(_ =>
        {
            y.Dispose();
            cbcr.Dispose();
            depth?.Dispose();
        });
        commands.Commit();
    }

    private CVMetalTexture? CreateTexture(CVPixelBuffer buffer, MTLPixelFormat format, int plane)
    {
        var width = plane == 0 && buffer.PlaneCount == 0 ? buffer.Width : buffer.GetWidthOfPlane(plane);
        var height = plane == 0 && buffer.PlaneCount == 0 ? buffer.Height : buffer.GetHeightOfPlane(plane);
        return _textureCache.TextureFromImage(buffer, format, width, height, plane, out _);
    }
}
