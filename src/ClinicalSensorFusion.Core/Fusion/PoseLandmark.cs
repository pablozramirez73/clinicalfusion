namespace ClinicalSensorFusion.Core.Fusion;

/// <summary>Indici dei 33 punti del modello MediaPipe Pose (BlazePose GHUM).</summary>
public static class PoseLandmark
{
    public const int Count = 33;

    public const int Nose = 0;
    public const int LeftShoulder = 11;
    public const int RightShoulder = 12;
    public const int LeftElbow = 13;
    public const int RightElbow = 14;
    public const int LeftWrist = 15;
    public const int RightWrist = 16;
    public const int LeftHip = 23;
    public const int RightHip = 24;
    public const int LeftKnee = 25;
    public const int RightKnee = 26;
    public const int LeftAnkle = 27;
    public const int RightAnkle = 28;
    public const int LeftFootIndex = 31;
    public const int RightFootIndex = 32;

    /// <summary>Segmenti per disegnare lo scheletro sull'anteprima.</summary>
    public static readonly (int From, int To)[] Connections =
    [
        (11, 12), (11, 13), (13, 15), (12, 14), (14, 16),
        (11, 23), (12, 24), (23, 24),
        (23, 25), (25, 27), (27, 29), (29, 31), (27, 31),
        (24, 26), (26, 28), (28, 30), (30, 32), (28, 32),
        (0, 2), (0, 5), (2, 7), (5, 8), (9, 10),
    ];
}

/// <summary>Indici dei 21 punti di MediaPipe Hands.</summary>
public static class HandLandmark
{
    public const int Count = 21;

    public static readonly (int From, int To)[] Connections =
    [
        (0, 1), (1, 2), (2, 3), (3, 4),
        (0, 5), (5, 6), (6, 7), (7, 8),
        (5, 9), (9, 10), (10, 11), (11, 12),
        (9, 13), (13, 14), (14, 15), (15, 16),
        (13, 17), (0, 17), (17, 18), (18, 19), (19, 20),
    ];
}

/// <summary>Punto 2D normalizzato [0,1] prodotto da MediaPipe.</summary>
public readonly record struct NormalizedLandmark(float X, float Y, float Z, float Visibility);
