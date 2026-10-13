using ClinicalSensorFusion.Core.Bluetooth;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Decodifica delle misure dei profili GATT standard (esempi dalle specifiche del Bluetooth SIG).</summary>
public class BluetoothTests
{
    [Fact]
    public void Heart_rate_with_8_bit_value_contact_and_rr_intervals()
    {
        // Flag 0x16: bpm a 8 bit, contatto rilevabile e presente, intervalli RR presenti.
        Assert.True(BleSensors.TryParse(BleSensors.HeartRateMeasurement, [0x16, 72, 0x00, 0x04, 0x33, 0x03], 1.5, out var r));
        Assert.Equal(SensorKind.HeartRate, r.Kind);
        Assert.Equal(1.5, r.Timestamp);
        Assert.Equal([72f, 1f, 1.0f, 819 / 1024f], r.Values);
    }

    [Fact]
    public void Heart_rate_with_16_bit_value_energy_and_no_contact_feature()
    {
        // Flag 0x09: bpm a 16 bit, energia presente (ignorata), nessun rilevamento del contatto.
        Assert.True(BleSensors.TryParse(BleSensors.HeartRateMeasurement, [0x09, 0x2C, 0x01, 0x10, 0x00], 0, out var r));
        Assert.Equal(300f, r.Values[0]);
        Assert.True(float.IsNaN(r.Values[1]));
        Assert.Equal(2, r.Values.Length);
    }

    [Fact]
    public void Pulse_oximeter_reads_sfloat_values_and_skips_not_available()
    {
        // SpO2 97 % e polso 61,5 bpm (mantissa 615, esponente −1).
        Assert.True(BleSensors.TryParse(BleSensors.PlxContinuousMeasurement, [0x00, 0x61, 0x00, 0x67, 0xF2], 0, out var r));
        Assert.Equal(SensorKind.PulseOximeter, r.Kind);
        Assert.Equal(97f, r.Values[0]);
        Assert.Equal(61.5f, r.Values[1], 1e-4f);

        // Dito non inserito: entrambi i valori "NaN" (0x07FF).
        Assert.False(BleSensors.TryParse(BleSensors.PlxSpotCheckMeasurement, [0x00, 0xFF, 0x07, 0xFF, 0x07], 0, out _));
    }

    [Fact]
    public void Thermometer_reads_celsius_and_converts_fahrenheit()
    {
        // 36,7 °C = mantissa 367, esponente −1.
        Assert.True(BleSensors.TryParse(BleSensors.TemperatureMeasurement, [0x00, 0x6F, 0x01, 0x00, 0xFF], 0, out var c));
        Assert.Equal(SensorKind.BodyTemperature, c.Kind);
        Assert.Equal(36.7f, c.Values[0], 1e-4f);

        // 98,6 °F = 37,0 °C.
        Assert.True(BleSensors.TryParse(BleSensors.TemperatureMeasurement, [0x01, 0xDA, 0x03, 0x00, 0xFF], 0, out var f));
        Assert.Equal(37.0f, f.Values[0], 1e-3f);
    }

    [Theory]
    [InlineData(BleSensors.HeartRateMeasurement, new byte[] { 0x00 })]
    [InlineData(BleSensors.HeartRateMeasurement, new byte[] { 0x01, 0x48 })]
    [InlineData(BleSensors.PlxContinuousMeasurement, new byte[] { 0x00, 0x61 })]
    [InlineData(BleSensors.TemperatureMeasurement, new byte[] { 0x00, 0xFF, 0xFF, 0x7F, 0x00 })]
    [InlineData((ushort)0x2A19, new byte[] { 90 })]
    public void Short_invalid_or_unknown_data_is_ignored(ushort characteristic, byte[] data) =>
        Assert.False(BleSensors.TryParse(characteristic, data, 0, out _));

    [Fact]
    public void Bluetooth_measurements_end_up_in_the_session_summary()
    {
        var aggregator = new SessionMetricsAggregator(CaptureMode.BodyLidar);
        foreach (var (t, bpm) in (ReadOnlySpan<(double, byte)>)[(0, 70), (1, 80), (2, 90)])
        {
            Assert.True(BleSensors.TryParse(BleSensors.HeartRateMeasurement, [0x00, bpm], t, out var reading));
            aggregator.Add(reading);
        }

        var summary = aggregator.Build(3, null, 0);
        var rate = Assert.Single(summary.Sensors![SensorKind.HeartRate].Channels);
        Assert.Equal(("Frequenza cardiaca", 70d, 90d, 80d, 3L), (rate.Name, rate.Min, rate.Max, rate.Mean, rate.Samples));
        Assert.Contains(
            SummaryReport.Build(summary, System.Globalization.CultureInfo.GetCultureInfo("it-IT")),
            r => r.Label == "Cardiofrequenzimetro (Bluetooth) · Frequenza cardiaca" && r.Value == "70–90 bpm (media 80, ultimo 90)");
    }
}
