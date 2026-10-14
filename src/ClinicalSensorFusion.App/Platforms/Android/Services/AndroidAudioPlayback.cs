using ClinicalSensorFusion.Core.Localization;
using Android.Media;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Signal;
using Stream = System.IO.Stream;

namespace ClinicalSensorFusion.App.Platforms.Android.Services;

/// <summary>
/// Riascolto delle registrazioni. Android non legge i file CAF: l'intestazione si interpreta qui e i
/// campioni Float32 vanno direttamente a un AudioTrack, senza conversioni né file temporanei.
/// </summary>
internal sealed class AndroidAudioPlayback : IAudioPlayback
{
    // Riascolto in corso: un oggetto nuovo a ogni Play, così un riascolto fermato non spegne quello successivo.
    private volatile object? _current;

    public bool IsPlaying => _current is not null;

    public void Play(string absolutePath)
    {
        StopPlayback();

        var file = File.OpenRead(absolutePath);
        AudioTrack track;
        CafInfo info;
        try
        {
            info = CafReader.ReadInfo(file);
            if (!info.IsFloat || !info.IsLittleEndian || info.BitsPerChannel != 32 || info.Channels is < 1 or > 2)
            {
                throw new NotSupportedException(Loc.T("Formato audio non riproducibile su questo dispositivo."));
            }

            var format = new AudioFormat.Builder()
                .SetEncoding(Encoding.PcmFloat)!
                .SetSampleRate((int)info.SampleRate)!
                .SetChannelMask(info.Channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo)!
                .Build()!;
            var attributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!;
            track = new AudioTrack.Builder()
                .SetAudioAttributes(attributes)
                .SetAudioFormat(format)
                .SetTransferMode(AudioTrackMode.Stream)
                .SetBufferSizeInBytes(Math.Max(AudioTrack.GetMinBufferSize((int)info.SampleRate, format.ChannelMask, Encoding.PcmFloat), 16 * 1024))
                .Build();
        }
        catch
        {
            file.Dispose();
            throw;
        }

        var session = _current = new object();
        _ = Task.Run(() => Pump(file, info, track, session));
    }

    public void StopPlayback() => _current = null;

    private void Pump(Stream file, CafInfo info, AudioTrack track, object session)
    {
        try
        {
            using (file)
            {
                var bytes = new byte[8192];
                var samples = new float[bytes.Length / sizeof(float)];
                var remaining = info.DataLength;
                track.Play();
                while (remaining > 0 && ReferenceEquals(_current, session))
                {
                    // Sempre un numero intero di campioni, anche se il file system restituisce letture parziali.
                    var wanted = (int)Math.Min(bytes.Length, remaining) & ~3;
                    var read = file.ReadAtLeast(bytes.AsSpan(0, wanted), wanted, throwOnEndOfStream: false) & ~3;
                    if (read <= 0)
                    {
                        break;
                    }

                    remaining -= read;
                    Buffer.BlockCopy(bytes, 0, samples, 0, read);
                    track.Write(samples, 0, read / sizeof(float), WriteMode.Blocking);
                }

                if (ReferenceEquals(_current, session))
                {
                    // Stop (e non Pause) lascia suonare quanto è ancora nel buffer.
                    track.Stop();
                }
            }
        }
        catch (Exception)
        {
            // File rimosso o uscita audio persa durante il riascolto: si chiude e basta.
        }
        finally
        {
            track.Release();
            track.Dispose();
            Interlocked.CompareExchange(ref _current, null, session);
        }
    }
}
