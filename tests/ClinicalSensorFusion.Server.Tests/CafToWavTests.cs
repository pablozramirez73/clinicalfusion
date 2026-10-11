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
    public void Rejects_non_caf_data() =>
        Assert.Throws<InvalidDataException>(() => CafToWav.Parse(new MemoryStream("RIFF0000WAVE"u8.ToArray())));
}
