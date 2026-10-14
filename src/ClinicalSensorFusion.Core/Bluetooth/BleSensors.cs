using System.Buffers.Binary;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.Core.Bluetooth;

/// <summary>Tipo di sensore Bluetooth Low Energy riconosciuto dal servizio GATT standard che espone.</summary>
public enum BleSensorType
{
    HeartRate,
    PulseOximeter,
    Thermometer,
}

/// <summary>Un sensore trovato durante la ricerca.</summary>
/// <param name="Id">Identificativo stabile del dispositivo su questo telefono (su iOS non è l'indirizzo MAC).</param>
/// <param name="Rssi">Potenza del segnale in dBm (più vicino a 0 = più vicino).</param>
public sealed record BleDevice(string Id, string Name, int Rssi, BleSensorType Type);

/// <summary>
/// Profili GATT standard del Bluetooth SIG supportati: funzionano con qualunque dispositivo che li rispetti
/// (fasce cardio, saturimetri, termometri), senza app o protocolli del produttore.
/// </summary>
public static class BleSensors
{
    /// <summary>Servizi (UUID a 16 bit) cercati durante la scansione, con il tipo di sensore.</summary>
    public static IReadOnlyDictionary<ushort, BleSensorType> Services { get; } = new Dictionary<ushort, BleSensorType>
    {
        [0x180D] = BleSensorType.HeartRate,
        [0x1822] = BleSensorType.PulseOximeter,
        [0x1809] = BleSensorType.Thermometer,
    };

    /// <summary>Caratteristiche di misura a cui abbonarsi (notifiche o indicazioni).</summary>
    public static IReadOnlySet<ushort> MeasurementCharacteristics { get; } = new HashSet<ushort>
    {
        HeartRateMeasurement, PlxContinuousMeasurement, PlxSpotCheckMeasurement, TemperatureMeasurement,
    };

    public const ushort HeartRateMeasurement = 0x2A37;
    public const ushort PlxSpotCheckMeasurement = 0x2A5E;
    public const ushort PlxContinuousMeasurement = 0x2A5F;
    public const ushort TemperatureMeasurement = 0x2A1C;

    public static string TypeName(BleSensorType type) => type switch
    {
        BleSensorType.HeartRate => "Frequenza cardiaca",
        BleSensorType.PulseOximeter => "Saturimetro",
        BleSensorType.Thermometer => "Termometro",
        _ => type.ToString(),
    };

    /// <summary>
    /// Decodifica una notifica di misura nel campione da archiviare. Falso se la caratteristica non è
    /// supportata o i dati sono troppo corti o privi di un valore valido.
    /// </summary>
    /// <param name="characteristic">UUID a 16 bit della caratteristica.</param>
    public static bool TryParse(ushort characteristic, ReadOnlySpan<byte> data, double timestamp, out SensorReading reading)
    {
        reading = default;
        float[]? values = characteristic switch
        {
            HeartRateMeasurement => HeartRate(data),
            PlxContinuousMeasurement or PlxSpotCheckMeasurement => PulseOximeter(data),
            TemperatureMeasurement => Temperature(data),
            _ => null,
        };
        if (values is null)
        {
            return false;
        }

        var kind = characteristic switch
        {
            HeartRateMeasurement => SensorKind.HeartRate,
            TemperatureMeasurement => SensorKind.BodyTemperature,
            _ => SensorKind.PulseOximeter,
        };
        reading = new SensorReading(kind, timestamp, values);
        return true;
    }

    /// <summary>Heart Rate Measurement: [flag][bpm u8 o u16][energia u16]?[RR u16 in 1/1024 s]*.</summary>
    private static float[]? HeartRate(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2)
        {
            return null;
        }

        var flags = d[0];
        var wide = (flags & 0x01) != 0;
        var offset = 1;
        if (d.Length < offset + (wide ? 2 : 1))
        {
            return null;
        }

        float bpm = wide ? BinaryPrimitives.ReadUInt16LittleEndian(d[offset..]) : d[offset];
        offset += wide ? 2 : 1;

        // Bit 2: il sensore sa rilevare il contatto con la pelle; bit 1: contatto presente.
        var contact = (flags & 0x04) != 0 ? ((flags & 0x02) != 0 ? 1f : 0f) : float.NaN;
        if ((flags & 0x08) != 0)
        {
            offset += 2; // energia consumata: non serve
        }

        var values = new List<float>(4) { bpm, contact };
        if ((flags & 0x10) != 0)
        {
            for (; offset + 2 <= d.Length; offset += 2)
            {
                values.Add(BinaryPrimitives.ReadUInt16LittleEndian(d[offset..]) / 1024f);
            }
        }

        return [.. values];
    }

    /// <summary>PLX (continua o a campione): [flag][SpO2 SFLOAT][frequenza del polso SFLOAT]…</summary>
    private static float[]? PulseOximeter(ReadOnlySpan<byte> d)
    {
        if (d.Length < 5)
        {
            return null;
        }

        var spo2 = SFloat(BinaryPrimitives.ReadUInt16LittleEndian(d[1..]));
        var pulse = SFloat(BinaryPrimitives.ReadUInt16LittleEndian(d[3..]));
        // I saturimetri mandano "non disponibile" finché il dito non è inserito bene.
        return float.IsFinite(spo2) || float.IsFinite(pulse) ? [spo2, pulse] : null;
    }

    /// <summary>Temperature Measurement: [flag][temperatura FLOAT]…; bit 0 del flag = gradi Fahrenheit.</summary>
    private static float[]? Temperature(ReadOnlySpan<byte> d)
    {
        if (d.Length < 5)
        {
            return null;
        }

        var raw = BinaryPrimitives.ReadUInt32LittleEndian(d[1..]);
        var mantissa = (int)(raw & 0x00FFFFFF);
        if (mantissa is 0x007FFFFF or 0x00800000 or 0x007FFFFE or 0x00800002)
        {
            return null; // NaN, NRes, ±infinito
        }

        if ((mantissa & 0x00800000) != 0)
        {
            mantissa |= unchecked((int)0xFF000000);
        }

        var value = mantissa * MathF.Pow(10, (sbyte)(raw >> 24));
        return [(d[0] & 0x01) != 0 ? (value - 32f) * 5f / 9f : value];
    }

    /// <summary>IEEE 11073 SFLOAT: esponente a 4 bit e mantissa a 12 bit, entrambi con segno.</summary>
    private static float SFloat(ushort raw)
    {
        var mantissa = raw & 0x0FFF;
        if (mantissa is 0x07FF or 0x0800 or 0x07FE or 0x0802 or 0x0801)
        {
            return float.NaN;
        }

        if ((mantissa & 0x0800) != 0)
        {
            mantissa -= 0x1000;
        }

        var exponent = raw >> 12;
        if ((exponent & 0x8) != 0)
        {
            exponent -= 0x10;
        }

        return mantissa * MathF.Pow(10, exponent);
    }
}
