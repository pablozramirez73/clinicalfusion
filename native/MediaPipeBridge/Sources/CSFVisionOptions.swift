import Foundation

/// Opzioni del motore di visione. Esposto a Objective-C (e quindi al binding .NET) come `CSFVisionOptions`.
/// Un percorso modello `nil` disattiva il relativo task: si caricano solo i modelli necessari
/// alla modalità di acquisizione, per risparmiare memoria e tempo di avvio.
@objc(CSFVisionOptions)
public final class CSFVisionOptions: NSObject {
    @objc public var poseModelPath: String?
    @objc public var handModelPath: String?
    @objc public var faceModelPath: String?
    @objc public var maxPoses: Int = 1
    @objc public var maxHands: Int = 2
    @objc public var maxFaces: Int = 1
    /// GPU (Metal) per l'inferenza; su iPhone 12 Pro è 2–3× più veloce della CPU.
    @objc public var useGpu: Bool = true
    @objc public var minConfidence: Float = 0.5
    /// Lato massimo dell'immagine passata ai modelli. I modelli lavorano a 256×256 / 192×192:
    /// inviare 1920×1440 costerebbe solo tempo di conversione.
    @objc public var maxInputDimension: Int = 640

    @objc public override init() {
        super.init()
    }
}
