namespace ClinicalSensorFusion.Core.Fusion;

/// <summary>
/// Copia gestita della mappa di profondità LiDAR (ARFrame.sceneDepth, tipicamente 256×192 Float32 in metri)
/// con la relativa confidenza (0 = bassa, 1 = media, 2 = alta). Orientamento nativo del sensore.
/// </summary>
public sealed class DepthMap
{
    public DepthMap(int width, int height, float[] depth, byte[]? confidence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (depth.Length < width * height)
        {
            throw new ArgumentException("Buffer di profondità troppo piccolo.", nameof(depth));
        }

        if (confidence is not null && confidence.Length < width * height)
        {
            throw new ArgumentException("Buffer di confidenza troppo piccolo.", nameof(confidence));
        }

        Width = width;
        Height = height;
        Depth = depth;
        Confidence = confidence;
    }

    public int Width { get; }

    public int Height { get; }

    public float[] Depth { get; }

    public byte[]? Confidence { get; }

    /// <summary>
    /// Profondità robusta (mediana) in un intorno quadrato di raggio <paramref name="radius"/> attorno al punto
    /// normalizzato (x, y). Scarta campioni non finiti, ≤ 0 o con confidenza inferiore a <paramref name="minConfidence"/>.
    /// La mediana è preferita alla media perché ai bordi del corpo l'intorno mescola soggetto e sfondo.
    /// </summary>
    public float SampleMedian(float x, float y, int radius = 2, byte minConfidence = 1)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0f || x > 1f || y < 0f || y > 1f)
        {
            return float.NaN;
        }

        var cx = Math.Clamp((int)(x * Width), 0, Width - 1);
        var cy = Math.Clamp((int)(y * Height), 0, Height - 1);

        Span<float> window = stackalloc float[(2 * radius + 1) * (2 * radius + 1)];
        var n = 0;
        for (var py = Math.Max(0, cy - radius); py <= Math.Min(Height - 1, cy + radius); py++)
        {
            var row = py * Width;
            for (var px = Math.Max(0, cx - radius); px <= Math.Min(Width - 1, cx + radius); px++)
            {
                var i = row + px;
                if (Confidence is not null && Confidence[i] < minConfidence)
                {
                    continue;
                }

                var d = Depth[i];
                if (float.IsFinite(d) && d > 0f)
                {
                    window[n++] = d;
                }
            }
        }

        if (n == 0)
        {
            return float.NaN;
        }

        var valid = window[..n];
        valid.Sort();
        return (n & 1) == 1 ? valid[n / 2] : 0.5f * (valid[n / 2 - 1] + valid[n / 2]);
    }

    /// <summary>Frazione di pixel con profondità valida e confidenza ≥ <paramref name="minConfidence"/>.</summary>
    public float ValidRatio(byte minConfidence = 1)
    {
        var total = Width * Height;
        var valid = 0;
        for (var i = 0; i < total; i++)
        {
            var d = Depth[i];
            if (float.IsFinite(d) && d > 0f && (Confidence is null || Confidence[i] >= minConfidence))
            {
                valid++;
            }
        }

        return valid / (float)total;
    }
}
