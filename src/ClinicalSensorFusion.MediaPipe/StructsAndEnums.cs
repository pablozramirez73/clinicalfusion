using ObjCRuntime;

namespace ClinicalSensorFusion.MediaPipe;

/// <summary>Codici d'errore del bridge (dominio NSError "MediaPipeBridge.CSFVisionErrorCode").</summary>
[Native]
public enum CSFVisionErrorCode : long
{
    NoTaskConfigured = 1,
    PixelBufferPoolFailed = 2,
    ConversionFailed = 3,
}
