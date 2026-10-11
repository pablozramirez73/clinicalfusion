using System.Numerics;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Tests;

public class FftTests
{
    [Fact]
    public void Sine_peaks_in_expected_bin()
    {
        const int n = 64;
        var data = new Complex[n];
        for (var i = 0; i < n; i++)
        {
            data[i] = Math.Sin(2 * Math.PI * 5 * i / n);
        }

        Fft.Forward(data);
        var peak = Enumerable.Range(1, n / 2 - 1).MaxBy(k => data[k].Magnitude);
        Assert.Equal(5, peak);
        Assert.Equal(n / 2.0, data[5].Magnitude, 1e-9);
    }

    [Fact]
    public void Rejects_non_power_of_two() => Assert.Throws<ArgumentException>(() => Fft.Forward(new Complex[6]));
}

public class TremorAnalyzerTests
{
    [Fact]
    public void Detects_5Hz_tremor_frequency_and_amplitude()
    {
        var analyzer = new TremorAnalyzer(sampleRateHz: 100);
        const double amplitudeG = 0.05;
        var rng = new Random(42);
        for (var i = 0; i < 3000; i++)
        {
            var t = i / 100.0;
            var noise = (rng.NextDouble() - 0.5) * 0.002;
            analyzer.AddScalar((float)(1 + amplitudeG * Math.Sin(2 * Math.PI * 5 * t) + noise));
        }

        var result = analyzer.Analyze();
        Assert.NotNull(result);
        Assert.InRange(result.DominantFrequencyHz, 4.8, 5.2);
        Assert.True(result.BandPowerRatio > 0.9, $"ratio {result.BandPowerRatio}");
        // RMS di una sinusoide = A/√2 → 0.05 g · 9.80665 / √2 ≈ 0.347 m/s².
        Assert.InRange(result.BandRmsMs2, 0.30, 0.39);
    }

    [Fact]
    public void Needs_at_least_one_window() => Assert.Null(new TremorAnalyzer().Analyze());
}

public class AudioFeatureAccumulatorTests
{
    [Fact]
    public void Half_scale_sine_is_minus_9_dBFS()
    {
        var acc = new AudioFeatureAccumulator(sampleRate: 48_000);
        var buffer = new float[48_000];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 48_000.0));
        }

        var (rms, peak) = acc.Add(buffer);
        var f = acc.Build()!;
        Assert.Equal(-9.03, rms, 0.05);            // 0.5/√2 → −9.03 dBFS
        Assert.Equal(0.5f, peak, 1e-3f);
        Assert.Equal(1.0, f.DurationSeconds, 1e-9);
        Assert.InRange(f.ZeroCrossingRate, 870, 890); // 2 × 440 Hz
        Assert.Equal(1.0, f.ActiveRatio, 1e-9);
        Assert.Equal(0.0, f.ClippingRatio);
    }

    [Fact]
    public void Silence_is_inactive()
    {
        var acc = new AudioFeatureAccumulator();
        acc.Add(new float[4800]);
        var f = acc.Build()!;
        Assert.Equal(0, f.ActiveRatio);
        Assert.True(f.RmsDbfs < -150);
    }
}
