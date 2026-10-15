using ClinicalSensorFusion.Core.Fusion;

namespace ClinicalSensorFusion.Core.Signal;

/// <summary>Ritmo del passo ricavato dai punti 3D della posa.</summary>
/// <param name="Steps">Passi riconosciuti.</param>
/// <param name="CadenceStepsPerMinute">Passi al minuto, dalla mediana degli intervalli tra un passo e il successivo.</param>
/// <param name="StepTimeCv">Variabilità della durata del passo (deviazione standard / media).</param>
public sealed record PoseGaitSummary(int Steps, double CadenceStepsPerMinute, double StepTimeCv);

/// <summary>
/// Passi dalla distanza tra le caviglie: è massima a ogni appoggio, quando un piede è avanti e l'altro dietro.
/// Usa solo la distanza tra due punti del corpo, quindi vale anche per i <i>world landmarks</i> di MediaPipe
/// (origine al centro delle anche, nessuno spostamento nella stanza) e non dipende da dove sta il telefono.
/// </summary>
/// <remarks>
/// Stesso metodo di <c>ml/fall_risk/features.py</c>: distanza normalizzata sulla lunghezza della gamba,
/// massimi distanti almeno 0,25 s e alti almeno 0,05 lunghezze di gamba rispetto ai minimi vicini; gli
/// intervalli oltre 1,5 s (pause, svolte) non sono passi.
/// </remarks>
public sealed class PoseStepAnalyzer
{
    private const double MinPeakSeconds = 0.25;
    private const double ProminenceWindowSeconds = 0.6;
    private const double MinProminence = 0.05;
    private const double MaxStepSeconds = 1.5;

    private readonly List<double> _t = [];
    private readonly List<double> _d = [];
    private readonly List<double> _leg = [];

    /// <summary>Un fotogramma: 33 punti × [x, y, z] in metri (layout di <c>PoseLandmarks3D</c>).</summary>
    public void Add(double timestamp, ReadOnlySpan<float> points)
    {
        if (points.Length < PoseLandmark.Count * 3)
        {
            return;
        }

        var ankles = Distance(points, PoseLandmark.LeftAnkle, PoseLandmark.RightAnkle);
        var leg = (Distance(points, PoseLandmark.LeftHip, PoseLandmark.LeftKnee) + Distance(points, PoseLandmark.LeftKnee, PoseLandmark.LeftAnkle)
                   + Distance(points, PoseLandmark.RightHip, PoseLandmark.RightKnee) + Distance(points, PoseLandmark.RightKnee, PoseLandmark.RightAnkle)) / 2;
        if (!double.IsFinite(ankles) || !double.IsFinite(leg) || leg < 0.2)
        {
            return;
        }

        _t.Add(timestamp);
        _d.Add(ankles);
        _leg.Add(leg);
    }

    /// <summary>Null se i passi riconosciuti non bastano a una stima (servono almeno tre passi).</summary>
    public PoseGaitSummary? Build()
    {
        if (_t.Count < 10)
        {
            return null;
        }

        var leg = Median(_leg);
        var d = Smooth(_d).Select(x => x / leg).ToArray();
        var peaks = new List<int>();
        for (var i = 0; i < d.Length; i++)
        {
            if (IsPeak(d, i))
            {
                peaks.Add(i);
            }
        }

        var intervals = new List<double>();
        for (var i = 1; i < peaks.Count; i++)
        {
            var dt = _t[peaks[i]] - _t[peaks[i - 1]];
            if (dt < MaxStepSeconds)
            {
                intervals.Add(dt);
            }
        }

        if (peaks.Count < 3 || intervals.Count < 2)
        {
            return null;
        }

        var mean = intervals.Average();
        var sd = Math.Sqrt(intervals.Sum(x => (x - mean) * (x - mean)) / intervals.Count);
        return new PoseGaitSummary(peaks.Count, 60 / Median(intervals), sd / mean);
    }

    private bool IsPeak(double[] d, int i)
    {
        // Massimo locale entro 0,25 s per parte; a parità tiene il primo.
        double left = double.PositiveInfinity, right = double.PositiveInfinity;
        for (var j = i - 1; j >= 0 && _t[i] - _t[j] <= ProminenceWindowSeconds; j--)
        {
            if (_t[i] - _t[j] <= MinPeakSeconds && d[j] >= d[i])
            {
                return false;
            }

            left = Math.Min(left, d[j]);
        }

        for (var j = i + 1; j < d.Length && _t[j] - _t[i] <= ProminenceWindowSeconds; j++)
        {
            if (_t[j] - _t[i] <= MinPeakSeconds && d[j] > d[i])
            {
                return false;
            }

            right = Math.Min(right, d[j]);
        }

        // Ai bordi della registrazione manca un lato: il picco non si può confermare.
        return double.IsFinite(left) && double.IsFinite(right) && d[i] - Math.Max(left, right) >= MinProminence;
    }

    /// <summary>Media mobile centrata su 5 fotogrammi: toglie il tremolio dei punti senza spostare i massimi.</summary>
    private static double[] Smooth(List<double> x)
    {
        var y = new double[x.Count];
        for (var i = 0; i < x.Count; i++)
        {
            var (from, to) = (Math.Max(0, i - 2), Math.Min(x.Count - 1, i + 2));
            double sum = 0;
            for (var j = from; j <= to; j++)
            {
                sum += x[j];
            }

            y[i] = sum / (to - from + 1);
        }

        return y;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Distance(ReadOnlySpan<float> p, int a, int b)
    {
        double dx = p[(a * 3)] - p[(b * 3)], dy = p[(a * 3) + 1] - p[(b * 3) + 1], dz = p[(a * 3) + 2] - p[(b * 3) + 2];
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
