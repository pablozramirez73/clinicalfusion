using ClinicalSensorFusion.Core.Fusion;
using CoreGraphics;
using CoreVideo;

namespace ClinicalSensorFusion.App.Platforms.iOS.Vision;

/// <summary>
/// Tutto ciò che serve all'inferenza di un fotogramma, estratto dall'ARFrame.
/// L'ARFrame non viene trattenuto: ARKit ha un pool di fotogrammi limitato e, se il codice li trattiene,
/// smette di consegnarne di nuovi ("delegate is retaining N ARFrames"). Si trattiene solo il pixel buffer
/// della camera (rilasciato a fine inferenza) e si copia la mappa di profondità (≈200 KB).
/// </summary>
internal sealed class ArFrameJob : IDisposable
{
    public required CVPixelBuffer Image { get; init; }

    /// <summary>Timestamp ARKit (system uptime, secondi).</summary>
    public required double Timestamp { get; init; }

    public DepthMap? Depth { get; init; }

    public required CameraIntrinsics Intrinsics { get; init; }

    /// <summary>Trasformazione da coordinate normalizzate dell'immagine a coordinate normalizzate della vista.</summary>
    public required CGAffineTransform DisplayTransform { get; init; }

    public required bool FrontCamera { get; init; }

    public void Dispose() => Image.Dispose();
}
