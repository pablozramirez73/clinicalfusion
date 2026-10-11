using CoreVideo;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace ClinicalSensorFusion.MediaPipe;

// Definizioni del binding: rispecchiano l'header Objective-C generato da Swift (MediaPipeBridge-Swift.h).
// Mantenere allineati i selettori con native/MediaPipeBridge/Sources/*.swift.

[BaseType(typeof(NSObject), Name = "CSFVisionOptions")]
interface CSFVisionOptions
{
    [NullAllowed, Export("poseModelPath")]
    string PoseModelPath { get; set; }

    [NullAllowed, Export("handModelPath")]
    string HandModelPath { get; set; }

    [NullAllowed, Export("faceModelPath")]
    string FaceModelPath { get; set; }

    [Export("maxPoses")]
    nint MaxPoses { get; set; }

    [Export("maxHands")]
    nint MaxHands { get; set; }

    [Export("maxFaces")]
    nint MaxFaces { get; set; }

    [Export("useGpu")]
    bool UseGpu { get; set; }

    [Export("minConfidence")]
    float MinConfidence { get; set; }

    [Export("maxInputDimension")]
    nint MaxInputDimension { get; set; }
}

[BaseType(typeof(NSObject), Name = "CSFVisionResult")]
[DisableDefaultCtor]
interface CSFVisionResult
{
    [Static, Export("valuesPerLandmark")]
    nint ValuesPerLandmark { get; }

    [Export("timestampMs")]
    nint TimestampMs { get; }

    [Export("inferenceMs")]
    double InferenceMs { get; }

    [Export("poseCount")]
    nint PoseCount { get; }

    [Export("poseLandmarks")]
    NSData PoseLandmarks { get; }

    [Export("poseWorldLandmarks")]
    NSData PoseWorldLandmarks { get; }

    [Export("handCount")]
    nint HandCount { get; }

    [Export("handLandmarks")]
    NSData HandLandmarks { get; }

    [Export("handWorldLandmarks")]
    NSData HandWorldLandmarks { get; }

    [Export("handedness")]
    string[] Handedness { get; }

    [Export("faceCount")]
    nint FaceCount { get; }

    [Export("faceLandmarks")]
    NSData FaceLandmarks { get; }

    [Export("faceBlendshapes")]
    NSData FaceBlendshapes { get; }
}

[BaseType(typeof(NSObject), Name = "CSFVisionEngine")]
[DisableDefaultCtor]
interface CSFVisionEngine
{
    [Static, Export("createWithOptions:error:")]
    [return: NullAllowed]
    CSFVisionEngine Create(CSFVisionOptions options, out NSError error);

    [Export("detectWithPixelBuffer:orientation:timestampMs:error:")]
    [return: NullAllowed]
    CSFVisionResult Detect(CVPixelBuffer pixelBuffer, UIImageOrientation orientation, nint timestampMs, out NSError error);
}
