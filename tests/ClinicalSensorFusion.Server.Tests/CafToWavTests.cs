using ClinicalSensorFusion.Server.Services;

namespace ClinicalSensorFusion.Server.Tests;

public class CafToWavTests
{
    [Fact]
    public void Parses_float32_caf_from_avaudiofile()
    {
        using var caf = new MemoryStream(SyncEndToEndTests.Caf(48));
        var info = CafToWav.Parse(caf);
        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(32, info.BitsPerChannel);
        Assert.True(info.IsFloat);
        Assert.True(info.LittleEndian);
        Assert.Equal(48 * 4, info.DataLength);
    }

    [Fact]
    public void Parses_caf_written_by_the_android_app()
    {
        var file = new MemoryStream();
        var writer = new Core.Signal.CafWriter(file, 48_000, 1);
        writer.Write(new float[48]);
        var bytes = file.ToArray();
        writer.Dispose();

        // Sia a registrazione in corso (dimensione −1) sia chiuso (dimensione definitiva).
        foreach (var caf in new[] { bytes, Closed(48) })
        {
            var info = CafToWav.Parse(new MemoryStream(caf));
            Assert.Equal(48000, info.SampleRate);
            Assert.Equal(1, info.Channels);
            Assert.Equal(32, info.BitsPerChannel);
            Assert.True(info.IsFloat);
            Assert.True(info.LittleEndian);
            Assert.Equal(48 * 4, info.DataLength);
        }
    }

    private static byte[] Closed(int frames)
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var writer = new Core.Signal.CafWriter(File.Create(path), 48_000, 1))
            {
                writer.Write(new float[frames]);
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rejects_non_caf_data() =>
        Assert.Throws<InvalidDataException>(() => CafToWav.Parse(new MemoryStream("RIFF0000WAVE"u8.ToArray())));
}
