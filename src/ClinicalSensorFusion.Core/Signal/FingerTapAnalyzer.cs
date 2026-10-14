namespace ClinicalSensorFusion.Core.Signal;

/// <summary>Tocchi pollice–indice rilevati: istante (secondi dall'inizio della registrazione) e ampiezza di ciascuno.</summary>
/// <param name="Amplitudes">
/// Apertura massima raggiunta prima di ogni tocco, in unità di "lunghezza del palmo" (distanza polso–base
/// dell'indice): un numero puro, indipendente dalla distanza della mano dalla fotocamera.
/// </param>
public sealed record FingerTapSeries(double[] Times, double[] Amplitudes);

/// <summary>Misure oggettive del finger tapping, ricavate dai tocchi avvenuti dopo il «Via».</summary>
/// <param name="AmplitudeDecrement">
/// Riduzione dell'ampiezza dai primi agli ultimi tre tocchi (0,3 = 30% in meno); null con meno di sei tocchi.
/// </param>
/// <param name="IntervalVariability">Coefficiente di variazione degli intervalli tra tocchi (irregolarità del ritmo).</param>
public sealed record FingerTapMetrics(int Taps, double? FrequencyHz, double? MeanAmplitude, double? AmplitudeDecrement, double? IntervalVariability)
{
    public static FingerTapMetrics From(FingerTapSeries series, double fromTime)
    {
        var times = new List<double>();
        var amplitudes = new List<double>();
        for (var i = 0; i < series.Times.Length && i < series.Amplitudes.Length; i++)
        {
            if (series.Times[i] >= fromTime)
            {
                times.Add(series.Times[i]);
                amplitudes.Add(series.Amplitudes[i]);
            }
        }

        var n = times.Count;
        if (n == 0)
        {
            return new FingerTapMetrics(0, null, null, null, null);
        }

        double? frequency = null;
        double? variability = null;
        if (n >= 2 && times[^1] > times[0])
        {
            frequency = (n - 1) / (times[^1] - times[0]);
            var intervals = new double[n - 1];
            for (var i = 1; i < n; i++)
            {
                intervals[i - 1] = times[i] - times[i - 1];
            }

            var mean = intervals.Average();
            if (intervals.Length >= 2 && mean > 0)
            {
                variability = Math.Sqrt(intervals.Sum(x => (x - mean) * (x - mean)) / (intervals.Length - 1)) / mean;
            }
        }

        double? decrement = null;
        if (n >= 6)
        {
            var first = amplitudes.Take(3).Average();
            var last = amplitudes.Skip(n - 3).Average();
            if (first > 0)
            {
                decrement = 1 - (last / first);
            }
        }

        return new FingerTapMetrics(n, frequency, amplitudes.Average(), decrement, variability);
    }
}

/// <summary>
/// Conta i tocchi pollice–indice dai punti della mano di MediaPipe. Un tocco è una chiusura (apertura sotto
/// <see cref="ClosedBelow"/>) preceduta da un'apertura (sopra <see cref="OpenAbove"/>): le due soglie
/// distanti evitano di contare il tremolio dei punti attorno a un solo valore.
/// </summary>
public sealed class FingerTapAnalyzer
{
    public const double ClosedBelow = 0.25;
    public const double OpenAbove = 0.40;

    // Indici MediaPipe Hands.
    private const int Wrist = 0;
    private const int ThumbTip = 4;
    private const int IndexBase = 5;
    private const int IndexTip = 8;
    private const int Points = 21;

    private readonly List<double> _times = [];
    private readonly List<double> _amplitudes = [];
    private bool _open;
    private double _peak;

    /// <summary>Rapporto larghezza/altezza dell'immagine del sensore: le coordinate sono normalizzate su lati diversi.</summary>
    public double AspectRatio { get; init; } = 4.0 / 3.0;

    public int Count => _times.Count;

    /// <summary>
    /// Aggiunge un fotogramma del flusso HandLandmarks:
    /// [n mani, poi per mano: lato, profondità del polso, 21 × (x, y, z)]. Si segue la prima mano.
    /// </summary>
    public void Add(double timestamp, ReadOnlySpan<float> values)
    {
        const int header = 1 + 2;
        if (values.Length < header + (Points * 3) || values[0] < 1)
        {
            return;
        }

        var hand = values.Slice(header, Points * 3);
        var palm = Distance(hand, Wrist, IndexBase);
        if (!(palm > 1e-4))
        {
            return;
        }

        AddAperture(timestamp, Distance(hand, ThumbTip, IndexTip) / palm);
    }

    /// <summary>Aggiunge direttamente un'apertura già normalizzata sulla lunghezza del palmo.</summary>
    public void AddAperture(double timestamp, double aperture)
    {
        if (!double.IsFinite(aperture))
        {
            return;
        }

        if (aperture > OpenAbove)
        {
            _open = true;
            _peak = Math.Max(_peak, aperture);
        }
        else if (aperture < ClosedBelow && _open)
        {
            _times.Add(timestamp);
            _amplitudes.Add(_peak);
            _open = false;
            _peak = 0;
        }
    }

    public FingerTapSeries? Build() => _times.Count > 0 ? new FingerTapSeries([.. _times], [.. _amplitudes]) : null;

    private double Distance(ReadOnlySpan<float> hand, int a, int b)
    {
        var dx = (hand[a * 3] - hand[b * 3]) * AspectRatio;
        var dy = hand[(a * 3) + 1] - hand[(b * 3) + 1];
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
