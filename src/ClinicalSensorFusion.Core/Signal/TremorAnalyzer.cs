using System.Numerics;

namespace ClinicalSensorFusion.Core.Signal;

/// <summary>Esito dell'analisi del tremore.</summary>
/// <param name="DominantFrequencyHz">Frequenza del picco spettrale nella banda del tremore.</param>
/// <param name="BandPowerRatio">Potenza nella banda del tremore / potenza totale 0.5–20 Hz (0..1).</param>
/// <param name="BandRmsMs2">Ampiezza RMS dell'accelerazione nella banda (m/s²).</param>
/// <param name="Windows">Finestre mediate (affidabilità della stima).</param>
public sealed record TremorResult(double DominantFrequencyHz, double BandPowerRatio, double BandRmsMs2, int Windows)
{
    /// <summary>Quota minima della potenza 0.5–20 Hz concentrata nella banda del tremore.</summary>
    public const double MinBandPowerRatio = 0.4;

    /// <summary>Ampiezza minima (m/s²): sotto, l'oscillazione è confrontabile con il rumore del sensore e il tremore fisiologico normale.</summary>
    public const double MinBandRmsMs2 = 0.05;

    /// <summary>
    /// Vero se nella banda c'è un'oscillazione distinguibile dal rumore. Il picco spettrale esiste sempre:
    /// senza questa soglia anche un arto fermo produrrebbe una "frequenza di tremore" (il picco del rumore).
    /// </summary>
    public bool IsSignificant => double.IsFinite(DominantFrequencyHz) && BandPowerRatio >= MinBandPowerRatio && BandRmsMs2 >= MinBandRmsMs2;
}

/// <summary>
/// Stima del tremore dall'accelerometro (accelerazione utente, gravità già rimossa da CoreMotion) con il
/// metodo di Welch: finestre di Hann sovrapposte al 50%, spettri di potenza mediati.
/// </summary>
/// <remarks>
/// Lo spettro è la <b>somma degli spettri dei tre assi</b>, non lo spettro del modulo |a|: l'accelerazione
/// utente ha media nulla e il modulo la raddrizzerebbe, spostando l'energia al doppio della frequenza
/// (|sin(2πft)| oscilla a 2f). La somma per assi è invece invariante all'orientamento del telefono.
/// </remarks>
/// <remarks>
/// La banda 3–12 Hz copre tremore parkinsoniano a riposo (4–6 Hz), essenziale (4–12 Hz) e fisiologico
/// potenziato (8–12 Hz). È un indicatore di supporto, non uno strumento diagnostico: dipende da come il
/// telefono è tenuto o fissato all'arto.
/// </remarks>
public sealed class TremorAnalyzer
{
    private const double StandardGravity = 9.80665;
    private readonly List<float>[] _axes = [[], [], []];

    public TremorAnalyzer(double sampleRateHz = 100, int windowSize = 512, double bandLowHz = 3, double bandHighHz = 12)
    {
        if (windowSize <= 0 || (windowSize & (windowSize - 1)) != 0)
        {
            throw new ArgumentException("windowSize deve essere una potenza di 2.", nameof(windowSize));
        }

        SampleRateHz = sampleRateHz;
        WindowSize = windowSize;
        BandLowHz = bandLowHz;
        BandHighHz = bandHighHz;
    }

    public double SampleRateHz { get; }

    public int WindowSize { get; }

    public double BandLowHz { get; }

    public double BandHighHz { get; }

    public int SampleCount => _axes[0].Count;

    /// <summary>Aggiunge un campione di accelerazione utente in g.</summary>
    public void Add(float ax, float ay, float az)
    {
        _axes[0].Add(ax);
        _axes[1].Add(ay);
        _axes[2].Add(az);
    }

    /// <summary>Aggiunge un campione scalare (g) su un solo asse. Per segnali già proiettati.</summary>
    public void AddScalar(float valueG) => Add(valueG, 0, 0);

    public TremorResult? Analyze()
    {
        if (SampleCount < WindowSize)
        {
            return null;
        }

        var hop = WindowSize / 2;
        var bins = WindowSize / 2 + 1;
        var psd = new double[bins];
        var window = new double[WindowSize];
        var windowPower = 0.0;
        for (var i = 0; i < WindowSize; i++)
        {
            window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (WindowSize - 1)));
            windowPower += window[i] * window[i];
        }

        var buffer = new Complex[WindowSize];
        var windows = 0;
        for (var start = 0; start + WindowSize <= SampleCount; start += hop)
        {
            foreach (var axis in _axes)
            {
                // Rimozione della media locale: la componente continua dominerebbe lo spettro.
                var mean = 0.0;
                for (var i = 0; i < WindowSize; i++)
                {
                    mean += axis[start + i];
                }

                mean /= WindowSize;
                for (var i = 0; i < WindowSize; i++)
                {
                    buffer[i] = new Complex((axis[start + i] - mean) * window[i], 0);
                }

                Fft.Forward(buffer);
                for (var k = 0; k < bins; k++)
                {
                    psd[k] += buffer[k].Magnitude * buffer[k].Magnitude;
                }
            }

            windows++;
        }

        var df = SampleRateHz / WindowSize;
        double band = 0, total = 0, peakPower = -1, peakFreq = double.NaN;
        for (var k = 1; k < bins; k++)
        {
            var f = k * df;
            // PSD unilatera normalizzata (unità g²/Hz), media sulle finestre.
            var p = psd[k] / (windows * windowPower * SampleRateHz) * (k == bins - 1 ? 1 : 2);
            if (f >= 0.5 && f <= 20)
            {
                total += p;
            }

            if (f >= BandLowHz && f <= BandHighHz)
            {
                band += p;
                if (p > peakPower)
                {
                    peakPower = p;
                    peakFreq = f;
                }
            }
        }

        if (total <= 0)
        {
            return new TremorResult(double.NaN, 0, 0, windows);
        }

        var bandRmsG = Math.Sqrt(band * df);
        return new TremorResult(peakFreq, band / total, bandRmsG * StandardGravity, windows);
    }
}
