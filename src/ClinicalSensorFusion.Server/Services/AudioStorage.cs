using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ClinicalSensorFusion.Server.Services;

public sealed class AudioStorageOptions
{
    /// <summary>Cartella dei file audio. Relativa alla cartella dell'app se non assoluta.</summary>
    public string Root { get; set; } = "data/audio";

    public long MaxFileBytes { get; set; } = 300L * 1024 * 1024;
}

/// <summary>
/// Archivio dei file CAF su disco. I file stanno fuori da wwwroot e si servono solo tramite endpoint
/// autenticati. Gli identificativi sono validati (32 caratteri esadecimali) prima di comporre i percorsi:
/// niente path traversal.
/// </summary>
public sealed partial class AudioStorage(IOptions<AudioStorageOptions> options, IHostEnvironment environment)
{
    private readonly string _root = Path.GetFullPath(Path.IsPathRooted(options.Value.Root)
        ? options.Value.Root
        : Path.Combine(environment.ContentRootPath, options.Value.Root));

    public long MaxFileBytes => options.Value.MaxFileBytes;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    public static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    /// <summary>Salva lo stream in modo atomico (file temporaneo + rename) e restituisce percorso relativo e dimensione.</summary>
    public async Task<(string RelativePath, long Size)> SaveAsync(string sessionId, string audioId, Stream content, CancellationToken ct)
    {
        if (!IsValidId(sessionId) || !IsValidId(audioId))
        {
            throw new ArgumentException("Identificativo non valido.");
        }

        var relative = Path.Combine(sessionId, audioId + ".caf");
        var target = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            long size;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var header = new byte[4];
                var read = await content.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct);
                if (read < 4 || header[0] != (byte)'c' || header[1] != (byte)'a' || header[2] != (byte)'f' || header[3] != (byte)'f')
                {
                    throw new InvalidDataException("Il file non è in formato CAF.");
                }

                await file.WriteAsync(header, ct);
                await content.CopyToAsync(file, ct);
                size = file.Length;
            }

            if (size > MaxFileBytes)
            {
                throw new InvalidDataException("File audio troppo grande.");
            }

            File.Move(temp, target, overwrite: true);
            return (relative, size);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public string? Resolve(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        return full.StartsWith(_root, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    public void DeleteSession(string sessionId)
    {
        if (!IsValidId(sessionId))
        {
            return;
        }

        var dir = Path.Combine(_root, sessionId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// Converte al volo un CAF PCM (quello registrato dall'app: Float32 little endian) in WAV, riproducibile da
/// tutti i browser (Chrome e Firefox non leggono il CAF). Non ricampiona: copia i campioni dopo una nuova intestazione.
/// </summary>
public static class CafToWav
{
    public sealed record CafInfo(double SampleRate, int Channels, int BitsPerChannel, bool IsFloat, bool LittleEndian, long DataOffset, long DataLength);

    public static CafInfo Parse(Stream caf)
    {
        Span<byte> header = stackalloc byte[8];
        caf.ReadExactly(header);
        if (header[0] != 'c' || header[1] != 'a' || header[2] != 'f' || header[3] != 'f')
        {
            throw new InvalidDataException("Non è un file CAF.");
        }

        double sampleRate = 0;
        int channels = 0, bits = 0;
        bool isFloat = false, little = false, haveDesc = false;
        Span<byte> chunkHeader = stackalloc byte[12];
        Span<byte> d = stackalloc byte[32];
        while (caf.Position + 12 <= caf.Length)
        {
            caf.ReadExactly(chunkHeader);
            var type = System.Text.Encoding.ASCII.GetString(chunkHeader[..4]);
            var size = BinaryPrimitives.ReadInt64BigEndian(chunkHeader[4..]);
            var start = caf.Position;

            if (type == "desc")
            {
                caf.ReadExactly(d);
                sampleRate = BinaryPrimitives.ReadDoubleBigEndian(d);
                var formatId = System.Text.Encoding.ASCII.GetString(d[8..12]);
                var flags = BinaryPrimitives.ReadUInt32BigEndian(d[12..]);
                channels = (int)BinaryPrimitives.ReadUInt32BigEndian(d[24..]);
                bits = (int)BinaryPrimitives.ReadUInt32BigEndian(d[28..]);
                if (formatId != "lpcm")
                {
                    throw new NotSupportedException($"Formato CAF non supportato: {formatId}.");
                }

                isFloat = (flags & 1) != 0;   // kCAFLinearPCMFormatFlagIsFloat
                little = (flags & 2) != 0;    // kCAFLinearPCMFormatFlagIsLittleEndian
                haveDesc = true;
            }
            else if (type == "data")
            {
                if (!haveDesc)
                {
                    throw new InvalidDataException("CAF senza descrizione del formato.");
                }

                // Il chunk "data" inizia con 4 byte di edit count. Dimensione -1 = fino a fine file.
                var dataOffset = start + 4;
                var dataLength = size == -1 ? caf.Length - dataOffset : size - 4;
                return new CafInfo(sampleRate, channels, bits, isFloat, little, dataOffset, dataLength);
            }

            if (size < 0)
            {
                break;
            }

            caf.Position = start + size;
        }

        throw new InvalidDataException("CAF senza dati audio.");
    }

    /// <summary>Scrive su <paramref name="output"/> l'intestazione WAV e i campioni (solo PCM little endian).</summary>
    public static async Task WriteWavAsync(Stream caf, Stream output, CancellationToken ct)
    {
        var info = Parse(caf);
        if (!info.LittleEndian)
        {
            throw new NotSupportedException("Solo CAF little endian.");
        }

        var header = BuildWavHeader(info);
        await output.WriteAsync(header, ct);
        caf.Position = info.DataOffset;
        var remaining = info.DataLength;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var read = await caf.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }

    public static long WavLength(CafInfo info) => 44 + info.DataLength;

    private static byte[] BuildWavHeader(CafInfo info)
    {
        var h = new byte[44];
        var blockAlign = info.Channels * info.BitsPerChannel / 8;
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)Math.Min(uint.MaxValue, 36 + info.DataLength));
        "WAVE"u8.CopyTo(h.AsSpan(8));
        "fmt "u8.CopyTo(h.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), (ushort)(info.IsFloat ? 3 : 1)); // 3 = IEEE float
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), (ushort)info.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)info.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)(info.SampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), (ushort)info.BitsPerChannel);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)Math.Min(uint.MaxValue, info.DataLength));
        return h;
    }
}
