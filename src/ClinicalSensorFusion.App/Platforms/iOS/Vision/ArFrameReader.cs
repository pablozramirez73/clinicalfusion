using ARKit;
using ClinicalSensorFusion.Core.Fusion;
using CoreVideo;

namespace ClinicalSensorFusion.App.Platforms.iOS.Vision;

/// <summary>Copie efficienti dei dati di un ARFrame verso strutture gestite.</summary>
internal static class ArFrameReader
{
    public static CameraIntrinsics Intrinsics(ARCamera camera)
    {
        // simd_float3x3 di ARKit è column-major: colonna 2 = (cx, cy, 1). NMatrix3 espone Mriga,colonna.
        var k = camera.Intrinsics;
        var size = camera.ImageResolution;
        return new CameraIntrinsics(k.M11, k.M22, k.M13, k.M23, (int)size.Width, (int)size.Height);
    }

    /// <summary>Copia la mappa LiDAR (Float32, metri) e la confidenza (UInt8 0..2).</summary>
    public static DepthMap? Depth(ARDepthData? data)
    {
        if (data?.DepthMap is not { } depthBuffer)
        {
            return null;
        }

        var width = (int)depthBuffer.Width;
        var height = (int)depthBuffer.Height;
        var depth = CopyPlane<float>(depthBuffer, width, height);
        var confidence = data.ConfidenceMap is { } c ? CopyPlane<byte>(c, width, height) : null;
        return new DepthMap(width, height, depth, confidence);
    }

    /// <summary>Profondità mediana al centro dell'immagine e frazione di pixel validi (flusso LidarDepth).</summary>
    public static float[] DepthSummary(DepthMap map) => [map.SampleMedian(0.5f, 0.5f, radius: 4), map.ValidRatio()];

    private static unsafe T[] CopyPlane<T>(CVPixelBuffer buffer, int width, int height)
        where T : unmanaged
    {
        var result = new T[width * height];
        buffer.Lock(CVPixelBufferLock.ReadOnly);
        try
        {
            var baseAddress = (byte*)buffer.BaseAddress;
            var stride = (int)buffer.BytesPerRow;
            for (var y = 0; y < height; y++)
            {
                // Le righe possono avere padding (BytesPerRow > width × sizeof(T)): copia riga per riga.
                new ReadOnlySpan<T>(baseAddress + (y * stride), width).CopyTo(result.AsSpan(y * width, width));
            }
        }
        finally
        {
            buffer.Unlock(CVPixelBufferLock.ReadOnly);
        }

        return result;
    }
}
