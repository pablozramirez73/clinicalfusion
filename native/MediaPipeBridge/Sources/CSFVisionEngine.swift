import CoreImage
import CoreVideo
import Foundation
import Metal
import MediaPipeTasksVision
import UIKit

@objc(CSFVisionErrorCode)
public enum CSFVisionErrorCode: Int, Error {
    case noTaskConfigured = 1
    case pixelBufferPoolFailed = 2
    case conversionFailed = 3
}

/// Facciata Objective-C sui task MediaPipe (Pose, Hands, Face Landmarker) in modalità VIDEO.
///
/// Perché un bridge Swift invece di legare direttamente MediaPipeTasksVision a .NET:
/// - l'API MediaPipe è ampia e cambia tra versioni; il binding .NET resta piccolo e stabile;
/// - la conversione YCbCr (ARKit) → BGRA ridimensionato richiesta da `MPImage` si fa qui con Core Image
///   su Metal, senza copie verso il mondo gestito;
/// - i framework statici di MediaPipe (con `-force_load` dei grafi) vengono incorporati in questo unico
///   framework dinamico: il progetto .NET referenzia un solo `.xcframework`.
///
/// Thread-safety: i landmarker MediaPipe non sono rientranti → `detect` è serializzato da un lock.
/// Il chiamante deve comunque scartare i fotogrammi mentre un'inferenza è in corso (back-pressure).
@objc(CSFVisionEngine)
public final class CSFVisionEngine: NSObject {
    private let pose: PoseLandmarker?
    private let hands: HandLandmarker?
    private let face: FaceLandmarker?
    private let maxInputDimension: Int
    private let ciContext: CIContext
    private let lock = NSLock()
    private var pool: CVPixelBufferPool?
    private var poolWidth = 0
    private var poolHeight = 0
    private var lastTimestampMs = -1

    /// Fabbrica con errore esplicito: in Objective-C diventa `+createWithOptions:error:`, più semplice da
    /// gestire nel binding .NET di un inizializzatore che può restituire nil.
    @objc(createWithOptions:error:)
    public static func create(options: CSFVisionOptions) throws -> CSFVisionEngine {
        try CSFVisionEngine(options: options)
    }

    public init(options: CSFVisionOptions) throws {
        let delegate: Delegate = options.useGpu ? .GPU : .CPU

        if let path = options.poseModelPath {
            let o = PoseLandmarkerOptions()
            o.baseOptions.modelAssetPath = path
            o.baseOptions.delegate = delegate
            o.runningMode = .video
            o.numPoses = options.maxPoses
            o.minPoseDetectionConfidence = options.minConfidence
            o.minPosePresenceConfidence = options.minConfidence
            o.minTrackingConfidence = options.minConfidence
            pose = try PoseLandmarker(options: o)
        } else {
            pose = nil
        }

        if let path = options.handModelPath {
            let o = HandLandmarkerOptions()
            o.baseOptions.modelAssetPath = path
            o.baseOptions.delegate = delegate
            o.runningMode = .video
            o.numHands = options.maxHands
            o.minHandDetectionConfidence = options.minConfidence
            o.minHandPresenceConfidence = options.minConfidence
            o.minTrackingConfidence = options.minConfidence
            hands = try HandLandmarker(options: o)
        } else {
            hands = nil
        }

        if let path = options.faceModelPath {
            let o = FaceLandmarkerOptions()
            o.baseOptions.modelAssetPath = path
            o.baseOptions.delegate = delegate
            o.runningMode = .video
            o.numFaces = options.maxFaces
            o.minFaceDetectionConfidence = options.minConfidence
            o.minFacePresenceConfidence = options.minConfidence
            o.minTrackingConfidence = options.minConfidence
            o.outputFaceBlendshapes = true
            face = try FaceLandmarker(options: o)
        } else {
            face = nil
        }

        if pose == nil && hands == nil && face == nil {
            throw CSFVisionErrorCode.noTaskConfigured
        }

        maxInputDimension = max(128, options.maxInputDimension)
        if let device = MTLCreateSystemDefaultDevice() {
            ciContext = CIContext(mtlDevice: device, options: [.cacheIntermediates: false])
        } else {
            ciContext = CIContext(options: [.cacheIntermediates: false])
        }

        super.init()
    }

    /// Esegue i task configurati su un fotogramma.
    /// - Parameters:
    ///   - pixelBuffer: immagine della camera, qualsiasi formato (ARKit: 420f YCbCr).
    ///   - orientation: rotazione per vedere l'immagine dritta (verticale, camera posteriore: `.right`).
    ///   - timestampMs: millisecondi monotoni crescenti (richiesto dalla modalità VIDEO).
    @objc(detectWithPixelBuffer:orientation:timestampMs:error:)
    public func detect(pixelBuffer: CVPixelBuffer, orientation: UIImage.Orientation, timestampMs: Int) throws -> CSFVisionResult {
        lock.lock()
        defer { lock.unlock() }

        let started = CACurrentMediaTime()
        // MediaPipe rifiuta timestamp non strettamente crescenti.
        let ts = timestampMs > lastTimestampMs ? timestampMs : lastTimestampMs + 1
        lastTimestampMs = ts

        let input = try prepare(pixelBuffer)
        let image = try MPImage(pixelBuffer: input, orientation: orientation)

        var poseCount = 0
        var poseLm = Data()
        var poseWorld = Data()
        if let pose {
            let r = try pose.detect(videoFrame: image, timestampInMilliseconds: ts)
            poseCount = r.landmarks.count
            poseLm = Self.pack(r.landmarks)
            poseWorld = Self.pack(r.worldLandmarks)
        }

        var handCount = 0
        var handLm = Data()
        var handWorld = Data()
        var handedness: [String] = []
        if let hands {
            let r = try hands.detect(videoFrame: image, timestampInMilliseconds: ts)
            handCount = r.landmarks.count
            handLm = Self.pack(r.landmarks)
            handWorld = Self.pack(r.worldLandmarks)
            handedness = r.handedness.map { $0.first?.categoryName ?? "" }
        }

        var faceCount = 0
        var faceLm = Data()
        var blend = Data()
        if let face {
            let r = try face.detect(videoFrame: image, timestampInMilliseconds: ts)
            faceCount = r.faceLandmarks.count
            faceLm = Self.pack(r.faceLandmarks)
            var scores: [Float] = []
            for classifications in r.faceBlendshapes {
                scores.append(contentsOf: classifications.categories.map { $0.score })
            }
            blend = scores.withUnsafeBufferPointer { Data(buffer: $0) }
        }

        return CSFVisionResult(
            timestampMs: ts,
            inferenceMs: (CACurrentMediaTime() - started) * 1000,
            poseCount: poseCount, poseLandmarks: poseLm, poseWorldLandmarks: poseWorld,
            handCount: handCount, handLandmarks: handLm, handWorldLandmarks: handWorld, handedness: handedness,
            faceCount: faceCount, faceLandmarks: faceLm, faceBlendshapes: blend)
    }

    // MARK: - Conversione immagine

    /// `MPImage` accetta solo BGRA: converte e ridimensiona con Core Image (GPU) in un buffer riciclato da un pool.
    private func prepare(_ source: CVPixelBuffer) throws -> CVPixelBuffer {
        let width = CVPixelBufferGetWidth(source)
        let height = CVPixelBufferGetHeight(source)
        let scale = min(1.0, Double(maxInputDimension) / Double(max(width, height)))
        let targetWidth = Int((Double(width) * scale).rounded())
        let targetHeight = Int((Double(height) * scale).rounded())

        if scale == 1.0 && CVPixelBufferGetPixelFormatType(source) == kCVPixelFormatType_32BGRA {
            return source
        }

        if pool == nil || poolWidth != targetWidth || poolHeight != targetHeight {
            let attributes: [String: Any] = [
                kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                kCVPixelBufferWidthKey as String: targetWidth,
                kCVPixelBufferHeightKey as String: targetHeight,
                kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any](),
                kCVPixelBufferMetalCompatibilityKey as String: true,
            ]
            var newPool: CVPixelBufferPool?
            let status = CVPixelBufferPoolCreate(kCFAllocatorDefault, [kCVPixelBufferPoolMinimumBufferCountKey as String: 3] as CFDictionary, attributes as CFDictionary, &newPool)
            guard status == kCVReturnSuccess, let newPool else { throw CSFVisionErrorCode.pixelBufferPoolFailed }
            pool = newPool
            poolWidth = targetWidth
            poolHeight = targetHeight
        }

        var output: CVPixelBuffer?
        guard let pool, CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &output) == kCVReturnSuccess, let output else {
            throw CSFVisionErrorCode.pixelBufferPoolFailed
        }

        var image = CIImage(cvPixelBuffer: source)
        if scale < 1.0 {
            image = image.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        }

        ciContext.render(image, to: output, bounds: CGRect(x: 0, y: 0, width: targetWidth, height: targetHeight), colorSpace: CGColorSpaceCreateDeviceRGB())
        return output
    }

    // MARK: - Impacchettamento

    private static func pack(_ groups: [[NormalizedLandmark]]) -> Data {
        var floats: [Float] = []
        floats.reserveCapacity(groups.reduce(0) { $0 + $1.count } * CSFVisionResult.valuesPerLandmark)
        for group in groups {
            for l in group {
                floats.append(l.x)
                floats.append(l.y)
                floats.append(l.z)
                floats.append(l.visibility?.floatValue ?? 1)
                floats.append(l.presence?.floatValue ?? 1)
            }
        }
        return floats.withUnsafeBufferPointer { Data(buffer: $0) }
    }

    private static func pack(_ groups: [[Landmark]]) -> Data {
        var floats: [Float] = []
        floats.reserveCapacity(groups.reduce(0) { $0 + $1.count } * CSFVisionResult.valuesPerLandmark)
        for group in groups {
            for l in group {
                floats.append(l.x)
                floats.append(l.y)
                floats.append(l.z)
                floats.append(l.visibility?.floatValue ?? 1)
                floats.append(l.presence?.floatValue ?? 1)
            }
        }
        return floats.withUnsafeBufferPointer { Data(buffer: $0) }
    }
}
