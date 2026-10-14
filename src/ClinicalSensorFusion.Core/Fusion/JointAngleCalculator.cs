using System.Numerics;

namespace ClinicalSensorFusion.Core.Fusion;

/// <summary>
/// Calcola gli angoli articolari da punti 3D (metri). Gli angoli 3D sono invarianti rispetto alla
/// posizione della camera, a differenza degli angoli 2D sull'immagine che dipendono dalla prospettiva:
/// è il motivo principale per fondere MediaPipe con il LiDAR.
/// </summary>
/// <remarks>
/// Convenzioni cliniche (goniometria):
/// <list type="bullet">
/// <item>Flessione gomito/anca/ginocchio = 180° − angolo interno (0° = segmento esteso).</item>
/// <item>Elevazione spalla = angolo tra tronco (spalla→anca) e braccio (spalla→gomito).</item>
/// <item>Caviglia = angolo interno gamba–piede (≈90° in posizione neutra).</item>
/// </list>
/// </remarks>
public static class JointAngleCalculator
{
    private readonly record struct Definition(int A, int Vertex, int C, bool FlexionFrom180);

    private static readonly Definition[] Definitions = BuildDefinitions();

    public static int JointCount => Definitions.Length;

    private static Definition[] BuildDefinitions()
    {
        var defs = new Definition[Enum.GetValues<Joint>().Length];
        defs[(int)Joint.LeftShoulderElevation] = new(PoseLandmark.LeftHip, PoseLandmark.LeftShoulder, PoseLandmark.LeftElbow, false);
        defs[(int)Joint.RightShoulderElevation] = new(PoseLandmark.RightHip, PoseLandmark.RightShoulder, PoseLandmark.RightElbow, false);
        defs[(int)Joint.LeftElbowFlexion] = new(PoseLandmark.LeftShoulder, PoseLandmark.LeftElbow, PoseLandmark.LeftWrist, true);
        defs[(int)Joint.RightElbowFlexion] = new(PoseLandmark.RightShoulder, PoseLandmark.RightElbow, PoseLandmark.RightWrist, true);
        defs[(int)Joint.LeftHipFlexion] = new(PoseLandmark.LeftShoulder, PoseLandmark.LeftHip, PoseLandmark.LeftKnee, true);
        defs[(int)Joint.RightHipFlexion] = new(PoseLandmark.RightShoulder, PoseLandmark.RightHip, PoseLandmark.RightKnee, true);
        defs[(int)Joint.LeftKneeFlexion] = new(PoseLandmark.LeftHip, PoseLandmark.LeftKnee, PoseLandmark.LeftAnkle, true);
        defs[(int)Joint.RightKneeFlexion] = new(PoseLandmark.RightHip, PoseLandmark.RightKnee, PoseLandmark.RightAnkle, true);
        defs[(int)Joint.LeftAnkle] = new(PoseLandmark.LeftKnee, PoseLandmark.LeftAnkle, PoseLandmark.LeftFootIndex, false);
        defs[(int)Joint.RightAnkle] = new(PoseLandmark.RightKnee, PoseLandmark.RightAnkle, PoseLandmark.RightFootIndex, false);
        return defs;
    }

    /// <summary>
    /// Calcola tutti gli angoli. <paramref name="points"/> contiene 33 punti; un punto con una
    /// componente NaN è considerato mancante e produce NaN negli angoli che lo usano.
    /// </summary>
    public static float[] Compute(ReadOnlySpan<Vector3> points)
    {
        if (points.Length < PoseLandmark.Count)
        {
            throw new ArgumentException($"Attesi {PoseLandmark.Count} punti, ricevuti {points.Length}.", nameof(points));
        }

        var result = new float[Definitions.Length];
        for (var i = 0; i < Definitions.Length; i++)
        {
            var d = Definitions[i];
            var interior = InteriorAngleDegrees(points[d.A], points[d.Vertex], points[d.C]);
            result[i] = d.FlexionFrom180 ? 180f - interior : interior;
        }

        return result;
    }

    /// <summary>Angolo in gradi nel vertice <paramref name="vertex"/> tra i segmenti verso a e c. NaN se degenere.</summary>
    public static float InteriorAngleDegrees(Vector3 a, Vector3 vertex, Vector3 c)
    {
        var u = a - vertex;
        var v = c - vertex;
        var lu = u.Length();
        var lv = v.Length();
        if (!float.IsFinite(lu) || !float.IsFinite(lv) || lu < 1e-6f || lv < 1e-6f)
        {
            return float.NaN;
        }

        var cos = Math.Clamp(Vector3.Dot(u, v) / (lu * lv), -1f, 1f);
        return MathF.Acos(cos) * (180f / MathF.PI);
    }
}
