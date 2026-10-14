namespace ClinicalSensorFusion.Core.Fusion;

/// <summary>
/// Parametri intrinseci della camera (modello pinhole) riferiti all'immagine catturata
/// (ARCamera.intrinsics + ARCamera.imageResolution), orientamento nativo del sensore (landscape).
/// </summary>
public readonly record struct CameraIntrinsics(float Fx, float Fy, float Cx, float Cy, int ImageWidth, int ImageHeight);
