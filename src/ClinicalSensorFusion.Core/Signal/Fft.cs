using System.Numerics;

namespace ClinicalSensorFusion.Core.Signal;

/// <summary>
/// FFT radix-2 iterativa in-place. Le finestre usate (≤ 4096 campioni) sono piccole: un'implementazione
/// semplice e senza allocazioni è più che sufficiente ed evita una dipendenza nativa (su iOS si potrebbe
/// usare vDSP/Accelerate, ma il Core deve restare testabile ovunque).
/// </summary>
public static class Fft
{
    public static void Forward(Span<Complex> data)
    {
        var n = data.Length;
        if (n == 0 || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("La lunghezza deve essere una potenza di 2.", nameof(data));
        }

        // Permutazione bit-reversal.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < len / 2; k++)
                {
                    var u = data[i + k];
                    var v = data[i + k + len / 2] * w;
                    data[i + k] = u + v;
                    data[i + k + len / 2] = u - v;
                    w *= wLen;
                }
            }
        }
    }
}
