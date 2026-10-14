import Foundation

/// Risultato di un fotogramma. I punti sono impacchettati in `Data` di Float32 per evitare migliaia di
/// oggetti Objective-C per fotogramma (il volto da solo ha 478 punti) e la relativa pressione sul GC .NET.
///
/// Layout di ogni punto: `[x, y, z, visibility, presence]` (`valuesPerLandmark` = 5).
/// - `*Landmarks`: x, y normalizzati 0…1 sull'immagine **originale** (quella del sensore, non ruotata); z relativo (scala ≈ x).
/// - `*WorldLandmarks`: metri, origine al centro delle anche (pose) o della mano.
/// - `faceBlendshapes`: 52 coefficienti 0…1 per volto, nell'ordine del modello MediaPipe.
@objc(CSFVisionResult)
public final class CSFVisionResult: NSObject {
    @objc public static let valuesPerLandmark: Int = 5

    @objc public let timestampMs: Int
    @objc public let inferenceMs: Double

    @objc public let poseCount: Int
    @objc public let poseLandmarks: Data
    @objc public let poseWorldLandmarks: Data

    @objc public let handCount: Int
    @objc public let handLandmarks: Data
    @objc public let handWorldLandmarks: Data
    /// "Left" / "Right" per ciascuna mano (lateralità come appare nell'immagine).
    @objc public let handedness: [String]

    @objc public let faceCount: Int
    @objc public let faceLandmarks: Data
    @objc public let faceBlendshapes: Data

    init(timestampMs: Int, inferenceMs: Double,
         poseCount: Int, poseLandmarks: Data, poseWorldLandmarks: Data,
         handCount: Int, handLandmarks: Data, handWorldLandmarks: Data, handedness: [String],
         faceCount: Int, faceLandmarks: Data, faceBlendshapes: Data) {
        self.timestampMs = timestampMs
        self.inferenceMs = inferenceMs
        self.poseCount = poseCount
        self.poseLandmarks = poseLandmarks
        self.poseWorldLandmarks = poseWorldLandmarks
        self.handCount = handCount
        self.handLandmarks = handLandmarks
        self.handWorldLandmarks = handWorldLandmarks
        self.handedness = handedness
        self.faceCount = faceCount
        self.faceLandmarks = faceLandmarks
        self.faceBlendshapes = faceBlendshapes
        super.init()
    }
}
