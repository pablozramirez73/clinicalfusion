using System.Numerics;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using ClinicalSensorFusion.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Services;

/// <summary>Fotogramma dello scheletro 3D per il replay: 33 × [x, y, z] (m), NaN → null lato JSON.</summary>
public sealed record PoseFrame(double T, float?[] Points, float?[] Angles);

public sealed record PoseReplay(int FrameCount, double Duration, string[] JointNames, int[][] Connections, IReadOnlyList<PoseFrame> Frames);

/// <summary>Spettrogramma: Power[t][f] in dB, frequenze in Hz, tempi in s.</summary>
public sealed record Spectrogram(double[] Times, double[] Frequencies, float[][] PowerDb, double SampleRateHz);

public sealed record StreamSeries(SensorKind Kind, int TotalSamples, double[] Times, float[][] Values);

/// <summary>
/// Decodifica i blocchi dei sensori e prepara serie compatte per i grafici 3D. Il sottocampionamento è sul
/// server: una sessione di 5 minuti ha 30.000 campioni IMU e 9.000 pose, troppi da spedire al browser.
/// </summary>
public sealed class SessionAnalytics(ClinicalDbContext db)
{
    public async Task<IReadOnlyList<SensorReading>> ReadStreamAsync(string sessionId, SensorKind kind, CancellationToken ct)
    {
        var payloads = await db.SensorChunks.AsNoTracking()
            .Where(c => c.SessionId == sessionId && c.Kind == kind)
            .OrderBy(c => c.Sequence)
            .Select(c => c.Payload)
            .ToListAsync(ct);

        var readings = new List<SensorReading>();
        foreach (var p in payloads)
        {
            readings.AddRange(SensorChunkCodec.Decode(kind, p));
        }

        return readings;
    }

    public async Task<PoseReplay> GetPoseReplayAsync(string sessionId, int maxFrames, CancellationToken ct)
    {
        var poses = await ReadStreamAsync(sessionId, SensorKind.PoseLandmarks3D, ct);
        var angles = await ReadStreamAsync(sessionId, SensorKind.JointAngles, ct);
        var step = Math.Max(1, (int)Math.Ceiling(poses.Count / (double)Math.Max(1, maxFrames)));

        var frames = new List<PoseFrame>(poses.Count / step + 1);
        var a = 0;
        for (var i = 0; i < poses.Count; i += step)
        {
            var p = poses[i];
            // Angoli del fotogramma più vicino (stesso timestamp nella pipeline dell'app).
            while (a + 1 < angles.Count && angles[a + 1].Timestamp <= p.Timestamp)
            {
                a++;
            }

            var angleValues = a < angles.Count ? angles[a].Values.Take(JointAngleCalculator.JointCount).ToArray() : [];
            frames.Add(new PoseFrame(p.Timestamp, ToNullable(p.Values), ToNullable(angleValues)));
        }

        var duration = poses.Count > 0 ? poses[^1].Timestamp - poses[0].Timestamp : 0;
        return new PoseReplay(
            frames.Count,
            duration,
            Enum.GetNames<Joint>(),
            [.. PoseLandmark.Connections.Select(c => new[] { c.From, c.To })],
            frames);
    }

    /// <summary>
    /// Spettrogramma (STFT, finestra di Hann) dell'accelerazione utente: mostra come evolve nel tempo
    /// l'energia alle varie frequenze. Un tremore appare come una "cresta" stabile tra 3 e 12 Hz.
    /// Potenza = somma dei tre assi (non il modulo, che raddoppierebbe la frequenza: vedi TremorAnalyzer).
    /// </summary>
    public async Task<Spectrogram> GetTremorSpectrogramAsync(string sessionId, int window, int maxColumns, double maxFrequencyHz, CancellationToken ct)
    {
        var motion = await ReadStreamAsync(sessionId, SensorKind.Motion, ct);
        if (motion.Count < window)
        {
            return new Spectrogram([], [], [], 0);
        }

        var duration = motion[^1].Timestamp - motion[0].Timestamp;
        var fs = duration > 0 ? (motion.Count - 1) / duration : 100;
        var axes = new float[3][];
        for (var a = 0; a < 3; a++)
        {
            var index = a;
            axes[a] = motion.Select(r => r.Values[index]).ToArray();
        }

        var length = motion.Count;

        var hop = Math.Max(window / 8, (length - window) / Math.Max(1, maxColumns - 1));
        var hann = new double[window];
        for (var i = 0; i < window; i++)
        {
            hann[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (window - 1)));
        }

        var df = fs / window;
        var bins = Math.Min(window / 2, (int)(maxFrequencyHz / df));
        var times = new List<double>();
        var power = new List<float[]>();
        var buffer = new Complex[window];
        var columnPower = new double[bins];
        for (var start = 0; start + window <= length; start += hop)
        {
            Array.Clear(columnPower);
            foreach (var signal in axes)
            {
                var mean = 0.0;
                for (var i = 0; i < window; i++)
                {
                    mean += signal[start + i];
                }

                mean /= window;
                for (var i = 0; i < window; i++)
                {
                    buffer[i] = new Complex((signal[start + i] - mean) * hann[i], 0);
                }

                Fft.Forward(buffer);
                for (var k = 1; k <= bins; k++)
                {
                    columnPower[k - 1] += buffer[k].Magnitude * buffer[k].Magnitude / window;
                }
            }

            var column = new float[bins];
            for (var k = 0; k < bins; k++)
            {
                column[k] = (float)(10 * Math.Log10(columnPower[k] + 1e-12));
            }

            power.Add(column);
            times.Add(motion[start + window / 2].Timestamp);
        }

        var freqs = Enumerable.Range(1, bins).Select(k => k * df).ToArray();
        return new Spectrogram([.. times], freqs, [.. power], fs);
    }

    /// <summary>Serie generica sottocampionata a <paramref name="maxPoints"/> (decimazione a passo costante).</summary>
    public async Task<StreamSeries> GetSeriesAsync(string sessionId, SensorKind kind, int maxPoints, CancellationToken ct)
    {
        var readings = await ReadStreamAsync(sessionId, kind, ct);
        var step = Math.Max(1, (int)Math.Ceiling(readings.Count / (double)Math.Max(1, maxPoints)));
        var times = new List<double>();
        var values = new List<float[]>();
        for (var i = 0; i < readings.Count; i += step)
        {
            times.Add(readings[i].Timestamp);
            values.Add(readings[i].Values);
        }

        return new StreamSeries(kind, readings.Count, [.. times], [.. values]);
    }

    private static float?[] ToNullable(float[] values)
    {
        var result = new float?[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = float.IsFinite(values[i]) ? MathF.Round(values[i], 4) : null;
        }

        return result;
    }
}
