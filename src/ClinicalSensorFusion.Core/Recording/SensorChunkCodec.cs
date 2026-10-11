using System.Buffers.Binary;
using System.IO.Compression;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.Core.Recording;

/// <summary>Blocco di campioni consecutivi di un flusso, come salvato nel DB.</summary>
public sealed record SensorChunk(SensorKind Kind, int Sequence, double StartTime, double EndTime, int SampleCount, byte[] Payload);

/// <summary>
/// Codifica binaria compatta dei campioni: i flussi ad alta frequenza (100 Hz × 16 valori, pose a 15–30 fps)
/// salvati riga per riga in SQLite produrrebbero milioni di righe; a blocchi compressi Brotli lo spazio
/// si riduce di circa 3–5 volte e le scritture sono poche e grandi.
/// </summary>
/// <remarks>
/// Formato (little endian), poi compresso con Brotli:
/// <c>[u8 versione=1] ripetuto: [f64 t][u16 n][f32 × n]</c>.
/// Il numero di valori è per campione perché alcuni flussi (mani) hanno lunghezza variabile.
/// </remarks>
public static class SensorChunkCodec
{
    private const byte Version = 1;

    public static byte[] Encode(IReadOnlyList<SensorReading> readings)
    {
        var size = 1;
        foreach (var r in readings)
        {
            if (r.Values.Length > ushort.MaxValue)
            {
                throw new ArgumentException("Troppi valori in un campione.", nameof(readings));
            }

            size += 8 + 2 + 4 * r.Values.Length;
        }

        var raw = new byte[size];
        raw[0] = Version;
        var offset = 1;
        foreach (var r in readings)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(raw.AsSpan(offset), r.Timestamp);
            offset += 8;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(offset), (ushort)r.Values.Length);
            offset += 2;
            foreach (var v in r.Values)
            {
                BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset), v);
                offset += 4;
            }
        }

        using var output = new MemoryStream(size / 2);
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(raw);
        }

        return output.ToArray();
    }

    public static List<SensorReading> Decode(SensorKind kind, byte[] payload)
    {
        using var input = new BrotliStream(new MemoryStream(payload), CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var raw = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

        if (raw.Length == 0 || raw[0] != Version)
        {
            throw new InvalidDataException("Versione del blocco sensori non supportata.");
        }

        var result = new List<SensorReading>();
        var offset = 1;
        while (offset < raw.Length)
        {
            var t = BinaryPrimitives.ReadDoubleLittleEndian(raw[offset..]);
            offset += 8;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(raw[offset..]);
            offset += 2;
            var values = new float[n];
            for (var i = 0; i < n; i++)
            {
                values[i] = BinaryPrimitives.ReadSingleLittleEndian(raw[offset..]);
                offset += 4;
            }

            result.Add(new SensorReading(kind, t, values));
        }

        return result;
    }
}
