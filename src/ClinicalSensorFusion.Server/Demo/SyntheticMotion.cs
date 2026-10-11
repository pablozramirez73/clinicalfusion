using System.Numerics;
using ClinicalSensorFusion.Core.Fusion;

namespace ClinicalSensorFusion.Server.Demo;

/// <summary>
/// Genera movimenti sintetici ma biomeccanicamente coerenti (squat con elevazione delle braccia) nel sistema
/// camera dell'app: X destra, Y giù, Z in avanti, soggetto di fronte al telefono a ~2,5 m.
/// Cinematica diretta: caviglia → ginocchio → anca → spalla → gomito → polso.
/// </summary>
internal static class SyntheticMotion
{
    private const float Shin = 0.43f, Thigh = 0.45f, Trunk = 0.52f, UpperArm = 0.30f, Forearm = 0.27f;
    private const float HalfHips = 0.11f, HalfShoulders = 0.19f;

    /// <param name="kneeFlex">Flessione del ginocchio (radianti).</param>
    /// <param name="shoulderElev">Elevazione anteriore della spalla (radianti).</param>
    /// <param name="elbowFlex">Flessione del gomito (radianti).</param>
    /// <param name="asymmetry">Riduzione relativa dell'escursione a sinistra (0 = simmetrico).</param>
    public static Vector3[] Pose(float kneeFlex, float shoulderElev, float elbowFlex, float asymmetry, float depth, Random rng, float noise)
    {
        var p = new Vector3[PoseLandmark.Count];
        const float floorY = 0.95f;

        void Leg(int hipIdx, int kneeIdx, int ankleIdx, int heelIdx, int footIdx, float side, float k)
        {
            var a = k / 2; // inclinazione della tibia
            var b = k - a; // inclinazione del femore → angolo interno = 180° − k
            var ankle = new Vector3(side * HalfHips, floorY, depth);
            var knee = ankle + Shin * new Vector3(0, -MathF.Cos(a), -MathF.Sin(a));
            var hip = knee + Thigh * new Vector3(0, -MathF.Cos(b), MathF.Sin(b));
            p[ankleIdx] = ankle;
            p[kneeIdx] = knee;
            p[hipIdx] = hip;
            p[heelIdx] = ankle + new Vector3(0, 0.04f, 0.05f);
            p[footIdx] = ankle + new Vector3(0, 0.05f, -0.16f);
        }

        Leg(PoseLandmark.LeftHip, PoseLandmark.LeftKnee, PoseLandmark.LeftAnkle, 29, PoseLandmark.LeftFootIndex, -1, kneeFlex * (1 - asymmetry));
        Leg(PoseLandmark.RightHip, PoseLandmark.RightKnee, PoseLandmark.RightAnkle, 30, PoseLandmark.RightFootIndex, 1, kneeFlex);

        var pelvis = (p[PoseLandmark.LeftHip] + p[PoseLandmark.RightHip]) / 2;
        var lean = 0.45f * kneeFlex / 2;
        var neck = pelvis + Trunk * new Vector3(0, -MathF.Cos(lean), -MathF.Sin(lean));

        void Arm(int shoulderIdx, int elbowIdx, int wristIdx, int pinky, int index, int thumb, float side, float e, float f)
        {
            var shoulder = neck + new Vector3(side * HalfShoulders, 0.02f, 0);
            var elbow = shoulder + UpperArm * new Vector3(0, MathF.Cos(e), -MathF.Sin(e));
            var wrist = elbow + Forearm * new Vector3(0, MathF.Cos(e + f), -MathF.Sin(e + f));
            p[shoulderIdx] = shoulder;
            p[elbowIdx] = elbow;
            p[wristIdx] = wrist;
            var hand = Vector3.Normalize(wrist - elbow) * 0.08f;
            p[pinky] = wrist + hand + new Vector3(side * 0.02f, 0, 0);
            p[index] = wrist + hand - new Vector3(side * 0.02f, 0, 0);
            p[thumb] = wrist + hand * 0.6f - new Vector3(side * 0.04f, 0, 0);
        }

        Arm(PoseLandmark.LeftShoulder, PoseLandmark.LeftElbow, PoseLandmark.LeftWrist, 17, 19, 21, -1, shoulderElev * (1 - asymmetry), elbowFlex);
        Arm(PoseLandmark.RightShoulder, PoseLandmark.RightElbow, PoseLandmark.RightWrist, 18, 20, 22, 1, shoulderElev, elbowFlex);

        // Testa: naso, occhi, orecchie, bocca attorno a un centro sopra il collo.
        var head = neck + new Vector3(0, -0.2f, -0.03f);
        p[0] = head + new Vector3(0, 0.02f, -0.1f);
        p[1] = head + new Vector3(-0.025f, -0.02f, -0.085f);
        p[2] = head + new Vector3(-0.035f, -0.02f, -0.085f);
        p[3] = head + new Vector3(-0.045f, -0.02f, -0.08f);
        p[4] = head + new Vector3(0.025f, -0.02f, -0.085f);
        p[5] = head + new Vector3(0.035f, -0.02f, -0.085f);
        p[6] = head + new Vector3(0.045f, -0.02f, -0.08f);
        p[7] = head + new Vector3(-0.08f, 0, 0);
        p[8] = head + new Vector3(0.08f, 0, 0);
        p[9] = head + new Vector3(-0.025f, 0.06f, -0.085f);
        p[10] = head + new Vector3(0.025f, 0.06f, -0.085f);

        for (var i = 0; i < p.Length; i++)
        {
            p[i] += new Vector3(Gauss(rng), Gauss(rng), 2.5f * Gauss(rng)) * noise; // il LiDAR è più rumoroso in profondità
        }

        return p;
    }

    private static float Gauss(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2 * Math.PI * u2));
    }
}
