using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.Core.Signal;

/// <summary>Formato dei campioni di un file CAF in PCM lineare.</summary>
/// <param name="DataOffset">Posizione nel file del primo campione.</param>
/// <param name="DataLength">Byte di campioni.</param>
public readonly record struct CafInfo(double SampleRate, int Channels, int BitsPerChannel, bool IsFloat, bool IsLittleEndian, long DataOffset, long DataLength);

/// <summary>
/// Scrive un file CAF (Core Audio Format) in PCM Float32 little-endian, lo stesso formato che su iOS produce
/// AVAudioFile. Serve dove il sistema non sa scrivere CAF (Android): così le registrazioni hanno un solo
/// formato per archivio, server e dashboard.
/// </summary>
/// <remarks>
/// Struttura: intestazione <c>caff</c>, chunk <c>desc</c> (formato), chunk <c>data</c> (campioni). La dimensione
/// del chunk dati si conosce solo alla fine: resta −1 ("fino a fine file", valore ammesso dal formato) finché
/// <see cref="Dispose"/> non la corregge, quindi un file troncato da un arresto anomalo resta leggibile.
/// </remarks>
public sealed class CafWriter : IDisposable
{
    private const int HeaderLength = 8 + 12 + 32 + 12 + 4;
    private const long DataSizeOffset = 8 + 12 + 32 + 4;

    private readonly Stream _stream;
    private long _dataBytes;

    public CafWriter(Stream stream, int sampleRate, int channels)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Scrittura CAF prevista solo su piattaforme little-endian.");
        }

        _stream = stream;
        Span<byte> h = stackalloc byte[HeaderLength];
        "caff"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt16BigEndian(h[4..], 1);          // versione
        BinaryPrimitives.WriteUInt16BigEndian(h[6..], 0);          // flag del file

        "desc"u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteInt64BigEndian(h[12..], 32);
        var d = h[20..];
        BinaryPrimitives.WriteDoubleBigEndian(d, sampleRate);
        "lpcm"u8.CopyTo(d[8..]);
        BinaryPrimitives.WriteUInt32BigEndian(d[12..], 3);         // float | little-endian
        BinaryPrimitives.WriteUInt32BigEndian(d[16..], (uint)(sizeof(float) * channels)); // byte per pacchetto
        BinaryPrimitives.WriteUInt32BigEndian(d[20..], 1);         // frame per pacchetto
        BinaryPrimitives.WriteUInt32BigEndian(d[24..], (uint)channels);
        BinaryPrimitives.WriteUInt32BigEndian(d[28..], 32);        // bit per canale

        "data"u8.CopyTo(h[52..]);
        BinaryPrimitives.WriteInt64BigEndian(h[56..], -1);
        BinaryPrimitives.WriteUInt32BigEndian(h[64..], 0);         // edit count
        _stream.Write(h);
    }

    /// <summary>Accoda campioni (interleaved se i canali sono più di uno).</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        var bytes = MemoryMarshal.AsBytes(samples);
        _stream.Write(bytes);
        _dataBytes += bytes.Length;
    }

    public void Dispose()
    {
        if (_stream.CanSeek)
        {
            Span<byte> size = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(size, _dataBytes + 4);
            _stream.Position = DataSizeOffset;
            _stream.Write(size);
        }

        _stream.Dispose();
    }
}

/// <summary>Lettura dell'intestazione di un file CAF in PCM lineare (per riascoltarlo senza AVFoundation).</summary>
public static class CafReader
{
    /// <summary>Legge il formato e lascia lo stream posizionato sul primo campione.</summary>
    public static CafInfo ReadInfo(Stream caf)
    {
        Span<byte> header = stackalloc byte[8];
        caf.ReadExactly(header);
        if (!header[..4].SequenceEqual("caff"u8))
        {
            throw new InvalidDataException(Loc.T("Non è un file CAF."));
        }

        CafInfo? format = null;
        Span<byte> chunk = stackalloc byte[12];
        Span<byte> d = stackalloc byte[32];
        while (caf.Position + 12 <= caf.Length)
        {
            caf.ReadExactly(chunk);
            var type = chunk[..4];
            var size = BinaryPrimitives.ReadInt64BigEndian(chunk[4..]);
            var start = caf.Position;

            if (type.SequenceEqual("desc"u8))
            {
                caf.ReadExactly(d);
                var formatId = Encoding.ASCII.GetString(d[8..12]);
                if (formatId != "lpcm")
                {
                    throw new NotSupportedException(Loc.F("Formato CAF non supportato: {0}.", formatId));
                }

                var flags = BinaryPrimitives.ReadUInt32BigEndian(d[12..]);
                format = new CafInfo(
                    BinaryPrimitives.ReadDoubleBigEndian(d),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(d[24..]),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(d[28..]),
                    IsFloat: (flags & 1) != 0,
                    IsLittleEndian: (flags & 2) != 0,
                    DataOffset: 0,
                    DataLength: 0);
            }
            else if (type.SequenceEqual("data"u8))
            {
                if (format is not { } f)
                {
                    throw new InvalidDataException(Loc.T("CAF senza descrizione del formato."));
                }

                // Il chunk "data" inizia con 4 byte di edit count. Dimensione −1 = fino a fine file.
                var offset = start + 4;
                caf.Position = offset;
                return f with { DataOffset = offset, DataLength = size == -1 ? caf.Length - offset : size - 4 };
            }

            caf.Position = start + size;
        }

        throw new InvalidDataException(Loc.T("CAF senza dati audio."));
    }
}
