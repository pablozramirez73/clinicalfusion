using System.Numerics;

namespace ClinicalSensorFusion.Core.Fusion;

/// <summary>Esito della fusione MediaPipe + LiDAR per un fotogramma.</summary>
/// <param name="Points">33 punti 3D in metri, sistema camera (X destra, Y giù, Z avanti). NaN se mancante.</param>
/// <param name="LidarJoints">Numero di punti rilevanti (spalle, gomiti, polsi, anche, ginocchia, caviglie) con profondità LiDAR valida.</param>
/// <param name="Source">Origine dei punti usati per gli angoli.</param>
/// <param name="SubjectDistance">Distanza mediana del tronco dalla camera (m), NaN se ignota.</param>
public sealed record FusedPose(Vector3[] Points, int LidarJoints, PoseSource Source, float SubjectDistance);

public enum PoseSource
{
    /// <summary>Punti 2D MediaPipe proiettati in 3D con la profondità LiDAR metrica.</summary>
    Lidar,

    /// <summary>Profondità insufficiente: "world landmarks" di MediaPipe (3D stimato dal modello, centrato sulle anche).</summary>
    MediaPipeWorld,
}

/// <summary>
/// Fonde i punti 2D di MediaPipe con la mappa di profondità LiDAR e gli intrinseci della camera,
/// ottenendo coordinate metriche reali.
/// </summary>
/// <remarks>
/// Retroproiezione pinhole: X = (u − cx)·Z / fx, Y = (v − cy)·Z / fy, con (u, v) in pixel dell'immagine catturata.
/// La mappa LiDAR (256×192) ha lo stesso campo visivo dell'immagine catturata (es. 1920×1440), quindi le
/// coordinate normalizzate sono condivise. Il LiDAR misura la superficie (pelle/abiti), non il centro
/// articolare: c'è un bias sistematico di qualche cm verso la camera, accettabile per gli angoli.
/// </remarks>
public sealed class PoseDepthFusion
{
    /// <summary>Punti necessari al calcolo degli angoli: se ne mancano troppi si usa il fallback MediaPipe.</summary>
    private static readonly int[] KeyJoints =
    [
        PoseLandmark.LeftShoulder, PoseLandmark.RightShoulder, PoseLandmark.LeftElbow, PoseLandmark.RightElbow,
        PoseLandmark.LeftWrist, PoseLandmark.RightWrist, PoseLandmark.LeftHip, PoseLandmark.RightHip,
        PoseLandmark.LeftKnee, PoseLandmark.RightKnee, PoseLandmark.LeftAnkle, PoseLandmark.RightAnkle,
    ];

    public float MinVisibility { get; init; } = 0.5f;

    public byte MinDepthConfidence { get; init; } = 1;

    /// <summary>Portata utile del LiDAR dell'iPhone (≈5 m); oltre si scarta.</summary>
    public float MaxDepthMeters { get; init; } = 5f;

    /// <summary>Frazione minima di punti chiave con profondità valida per preferire il LiDAR.</summary>
    public float MinLidarKeyJointRatio { get; init; } = 0.75f;

    /// <param name="landmarks">33 punti MediaPipe in coordinate orientate.</param>
    /// <param name="worldLandmarks">33 "world landmarks" MediaPipe (m), opzionali per il fallback.</param>
    public FusedPose Fuse(
        ReadOnlySpan<NormalizedLandmark> landmarks,
        ReadOnlySpan<Vector3> worldLandmarks,
        ImageOrientation orientation,
        DepthMap depth,
        CameraIntrinsics intrinsics)
    {
        if (landmarks.Length < PoseLandmark.Count)
        {
            throw new ArgumentException($"Attesi {PoseLandmark.Count} punti.", nameof(landmarks));
        }

        var points = new Vector3[PoseLandmark.Count];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = Unproject(landmarks[i], orientation, depth, intrinsics);
        }

        var lidarJoints = 0;
        foreach (var j in KeyJoints)
        {
            if (float.IsFinite(points[j].Z))
            {
                lidarJoints++;
            }
        }

        var distance = TrunkDistance(points);

        if (lidarJoints >= MinLidarKeyJointRatio * KeyJoints.Length || worldLandmarks.Length < PoseLandmark.Count)
        {
            return new FusedPose(points, lidarJoints, PoseSource.Lidar, distance);
        }

        // Fallback: i world landmarks MediaPipe sono metrici ma con profondità stimata dal modello (meno accurata).
        // Si mantengono solo i punti visibili, coerentemente con il ramo LiDAR.
        var world = new Vector3[PoseLandmark.Count];
        for (var i = 0; i < world.Length; i++)
        {
            world[i] = landmarks[i].Visibility >= MinVisibility ? worldLandmarks[i] : new Vector3(float.NaN);
        }

        return new FusedPose(world, lidarJoints, PoseSource.MediaPipeWorld, distance);
    }

    /// <summary>Retroproietta un singolo punto. Restituisce un vettore NaN se non valido.</summary>
    public Vector3 Unproject(NormalizedLandmark lm, ImageOrientation orientation, DepthMap depth, CameraIntrinsics k)
    {
        if (lm.Visibility < MinVisibility)
        {
            return new Vector3(float.NaN);
        }

        var (x, y) = OrientationMapper.ToSensor(lm.X, lm.Y, orientation);
        var z = depth.SampleMedian(x, y, radius: 2, MinDepthConfidence);
        if (!float.IsFinite(z) || z > MaxDepthMeters)
        {
            return new Vector3(float.NaN);
        }

        var u = x * k.ImageWidth;
        var v = y * k.ImageHeight;
        return new Vector3((u - k.Cx) * z / k.Fx, (v - k.Cy) * z / k.Fy, z);
    }

    private static float TrunkDistance(Vector3[] p)
    {
        Span<float> z = stackalloc float[4];
        var n = 0;
        foreach (var i in (ReadOnlySpan<int>)[PoseLandmark.LeftShoulder, PoseLandmark.RightShoulder, PoseLandmark.LeftHip, PoseLandmark.RightHip])
        {
            if (float.IsFinite(p[i].Z))
            {
                z[n++] = p[i].Z;
            }
        }

        if (n == 0)
        {
            return float.NaN;
        }

        var s = z[..n];
        s.Sort();
        return (n & 1) == 1 ? s[n / 2] : 0.5f * (s[n / 2 - 1] + s[n / 2]);
    }
}
