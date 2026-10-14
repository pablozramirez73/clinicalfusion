using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Tests;

public class CafFileTests
{
    [Fact]
    public void Written_file_reads_back_with_same_format_and_samples()
    {
        float[] samples = [0f, 0.5f, -0.5f, 1f, -1f];
        var buffer = new MemoryStream();
        using (var writer = new CafWriter(new NonClosing(buffer), 48_000, 1))
        {
            writer.Write(samples.AsSpan(0, 2));
            writer.Write(samples.AsSpan(2));
        }

        buffer.Position = 0;
        var info = CafReader.ReadInfo(buffer);
        Assert.Equal(48_000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(32, info.BitsPerChannel);
        Assert.True(info.IsFloat);
        Assert.True(info.IsLittleEndian);
        Assert.Equal(samples.Length * sizeof(float), info.DataLength);
        Assert.Equal(info.DataOffset, buffer.Position);

        var read = new byte[info.DataLength];
        buffer.ReadExactly(read);
        Assert.Equal(samples, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(read).ToArray());
    }

    [Fact]
    public void File_cut_before_close_is_read_up_to_the_end()
    {
        var buffer = new MemoryStream();
        var writer = new CafWriter(buffer, 48_000, 1);
        writer.Write([0.25f, 0.5f, 0.75f]);

        // Nessun Dispose: come dopo un arresto anomalo, la dimensione del chunk dati è ancora −1.
        var truncated = new MemoryStream(buffer.ToArray());
        Assert.Equal(3 * sizeof(float), CafReader.ReadInfo(truncated).DataLength);
    }

    [Fact]
    public void Rejects_non_caf_data() =>
        Assert.Throws<InvalidDataException>(() => CafReader.ReadInfo(new MemoryStream("RIFF0000WAVEfmt "u8.ToArray())));

    /// <summary>Il writer chiude lo stream: qui serve rileggerlo.</summary>
    private sealed class NonClosing(MemoryStream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

public class FaceBlendshapeLayoutTests
{
    [Fact]
    public void Names_are_the_52_arkit_coefficients_in_ordinal_order()
    {
        Assert.Equal(52, FaceBlendshapeLayout.Names.Count);
        Assert.Equal(FaceBlendshapeLayout.Names.Order(StringComparer.Ordinal), FaceBlendshapeLayout.Names);
    }

    [Fact]
    public void Mediapipe_scores_land_on_their_name_and_missing_ones_stay_nan()
    {
        // Ordine di MediaPipe: "_neutral" per primo, poi nomi non alfabetici.
        string[] names = ["_neutral", "jawOpen", "browDownLeft", "eyeBlinkRight"];
        var values = FaceBlendshapeLayout.FromNamed(names, [0.9f, 0.4f, 0.1f, 0.7f]);

        Assert.Equal(52, values.Length);
        Assert.Equal(0.1f, values[0]);
        Assert.Equal(0.7f, values[IndexOf("eyeBlinkRight")]);
        Assert.Equal(0.4f, values[IndexOf("jawOpen")]);
        Assert.True(float.IsNaN(values[IndexOf("tongueOut")]));
        Assert.Equal(49, values.Count(float.IsNaN));
    }

    private static int IndexOf(string name) => FaceBlendshapeLayout.Names.ToList().IndexOf(name);
}
