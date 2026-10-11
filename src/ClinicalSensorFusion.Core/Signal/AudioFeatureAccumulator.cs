namespace ClinicalSensorFusion.Core.Signal;

/// <summary>Indicatori acustici di una registrazione.</summary>
/// <param name="DurationSeconds">Durata.</param>
/// <param name="RmsDbfs">Livello RMS complessivo (dBFS).</param>
/// <param name="PeakDbfs">Picco (dBFS).</param>
/// <param name="ZeroCrossingRate">Attraversamenti dello zero al secondo (proxy grezzo del contenuto in frequenza).</param>
/// <param name="ActiveRatio">Frazione di blocchi da 20 ms sopra la soglia di attività (fonazione/respiro udibile).</param>
/// <param name="ClippingRatio">Frazione di campioni saturati (|x| ≥ 0.999): qualità della registrazione.</param>
public sealed record AudioFeatures(
    double DurationSeconds,
    double RmsDbfs,
    double PeakDbfs,
    double ZeroCrossingRate,
    double ActiveRatio,
    double ClippingRatio);

/// <summary>
/// Calcolo incrementale degli indicatori sui buffer Float32 del microfono, durante la registrazione:
/// nessuna rilettura del file CAF a fine sessione.
/// </summary>
public sealed class AudioFeatureAccumulator
{
    private readonly int _sampleRate;
    private readonly int _blockSize;
    private readonly double _activityThresholdDbfs;

    private long _samples;
    private double _sumSquares;
    private float _peak;
    private long _zeroCrossings;
    private long _clipped;
    private float _previous;

    private double _blockSumSquares;
    private int _blockFill;
    private long _blocks;
    private long _activeBlocks;

    public AudioFeatureAccumulator(int sampleRate = 48_000, double blockSeconds = 0.020, double activityThresholdDbfs = -45)
    {
        _sampleRate = sampleRate;
        _blockSize = Math.Max(1, (int)(sampleRate * blockSeconds));
        _activityThresholdDbfs = activityThresholdDbfs;
    }

    /// <summary>Elabora un buffer e restituisce RMS (dBFS) e picco del buffer, utili per il VU-meter live.</summary>
    public (float RmsDbfs, float Peak) Add(ReadOnlySpan<float> buffer)
    {
        double bufferSquares = 0;
        float bufferPeak = 0;
        foreach (var s in buffer)
        {
            var a = MathF.Abs(s);
            var sq = (double)s * s;
            bufferSquares += sq;
            if (a > bufferPeak)
            {
                bufferPeak = a;
            }

            if (a >= 0.999f)
            {
                _clipped++;
            }

            if ((s >= 0) != (_previous >= 0))
            {
                _zeroCrossings++;
            }

            _previous = s;

            _blockSumSquares += sq;
            if (++_blockFill == _blockSize)
            {
                _blocks++;
                if (ToDb(Math.Sqrt(_blockSumSquares / _blockSize)) > _activityThresholdDbfs)
                {
                    _activeBlocks++;
                }

                _blockSumSquares = 0;
                _blockFill = 0;
            }
        }

        _samples += buffer.Length;
        _sumSquares += bufferSquares;
        _peak = MathF.Max(_peak, bufferPeak);
        var rms = buffer.Length == 0 ? 0 : Math.Sqrt(bufferSquares / buffer.Length);
        return ((float)ToDb(rms), bufferPeak);
    }

    public AudioFeatures? Build()
    {
        if (_samples == 0)
        {
            return null;
        }

        var duration = _samples / (double)_sampleRate;
        return new AudioFeatures(
            DurationSeconds: duration,
            RmsDbfs: ToDb(Math.Sqrt(_sumSquares / _samples)),
            PeakDbfs: ToDb(_peak),
            ZeroCrossingRate: _zeroCrossings / duration,
            ActiveRatio: _blocks == 0 ? 0 : _activeBlocks / (double)_blocks,
            ClippingRatio: _clipped / (double)_samples);
    }

    private static double ToDb(double linear) => linear <= 1e-10 ? -200 : 20 * Math.Log10(linear);
}
