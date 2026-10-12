namespace ClinicalSensorFusion.Core.Fusion;

/// <summary>
/// Layout del flusso <see cref="Sensors.SensorKind.FaceBlendshapes"/>: i 52 coefficienti di ARKit in ordine
/// alfabetico del nome. Su iOS li fornisce la fotocamera TrueDepth; dove non c'è (Android) arrivano da
/// MediaPipe Face Landmarker, che usa gli stessi nomi ma un altro ordine, un coefficiente in più
/// (<c>_neutral</c>) e non stima <c>tongueOut</c>.
/// </summary>
public static class FaceBlendshapeLayout
{
    /// <summary>Nomi nell'ordine del flusso (ordinamento ordinale, come le chiavi di ARKit).</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight",
        "cheekPuff", "cheekSquintLeft", "cheekSquintRight",
        "eyeBlinkLeft", "eyeBlinkRight", "eyeLookDownLeft", "eyeLookDownRight", "eyeLookInLeft", "eyeLookInRight",
        "eyeLookOutLeft", "eyeLookOutRight", "eyeLookUpLeft", "eyeLookUpRight", "eyeSquintLeft", "eyeSquintRight",
        "eyeWideLeft", "eyeWideRight",
        "jawForward", "jawLeft", "jawOpen", "jawRight",
        "mouthClose", "mouthDimpleLeft", "mouthDimpleRight", "mouthFrownLeft", "mouthFrownRight", "mouthFunnel",
        "mouthLeft", "mouthLowerDownLeft", "mouthLowerDownRight", "mouthPressLeft", "mouthPressRight", "mouthPucker",
        "mouthRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper",
        "mouthSmileLeft", "mouthSmileRight", "mouthStretchLeft", "mouthStretchRight", "mouthUpperUpLeft", "mouthUpperUpRight",
        "noseSneerLeft", "noseSneerRight",
        "tongueOut",
    ];

    /// <summary>
    /// Riordina i coefficienti di MediaPipe nel layout del flusso. I nomi sconosciuti si ignorano; quelli
    /// che MediaPipe non produce restano NaN ("non misurato", diverso da 0 = "a riposo").
    /// </summary>
    public static float[] FromNamed(IReadOnlyList<string> names, ReadOnlySpan<float> scores)
    {
        var values = new float[Names.Count];
        Array.Fill(values, float.NaN);
        var n = Math.Min(names.Count, scores.Length);
        for (var i = 0; i < n; i++)
        {
            var index = IndexOf(names[i]);
            if (index >= 0)
            {
                values[index] = scores[i];
            }
        }

        return values;
    }

    private static int IndexOf(string name)
    {
        // Ricerca binaria: Names è ordinato in modo ordinale (verificato dai test).
        int lo = 0, hi = Names.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var c = string.CompareOrdinal(Names[mid], name);
            if (c == 0)
            {
                return mid;
            }

            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }
}
