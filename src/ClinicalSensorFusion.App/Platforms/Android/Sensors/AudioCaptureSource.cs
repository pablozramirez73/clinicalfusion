using Android.Content;
using Android.Media;
using Android.OS;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Platforms.Android.Sensors;

/// <summary>
/// Microfono → <c>audio/&lt;id&gt;/audio.caf</c>, CAF mono 48 kHz Float32 non compresso: lo stesso formato
/// dell'app iOS (≈11,5 MB/min), scritto da <see cref="CafWriter"/> perché Android non produce CAF.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Sorgente <c>UNPROCESSED</c> dove c'è (altrimenti <c>VOICE_RECOGNITION</c>): niente controllo
/// automatico del guadagno né riduzione del rumore, come la modalità <i>measurement</i> di iOS. Per
/// un'analisi acustica clinica serve il segnale "vero", non quello ottimizzato per le chiamate.</item>
/// <item>Gli indicatori (RMS, picco, attività, clipping) si calcolano sui buffer in arrivo: a fine
/// registrazione sono già pronti senza rileggere il file.</item>
/// <item>Il file sta nella cartella privata dell'app, cifrata dal sistema e mai inclusa nei backup.</item>
/// </list>
/// </remarks>
internal sealed class AudioCaptureSource(SensorClock clock, string sessionId, ILogger logger) : ISensorSource
{
    private const long MinFreeBytes = 200L * 1024 * 1024;
    private const int BufferFrames = 4096;
    private const int ReadBlocking = 0; // AudioRecord.READ_BLOCKING

    private readonly AudioFeatureAccumulator _features = new(AudioRecording.SampleRate);
    private AudioRecord? _record;
    private CafWriter? _file;
    private Thread? _thread;
    private AudioRecording? _recording;
    private ISensorSink? _sink;
    private long _framesWritten;
    private volatile bool _running;

    public string DisplayName => "Microfono";

    public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.AudioLevel];

    public bool IsAvailable =>
        global::Android.App.Application.Context.PackageManager?.HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureMicrophone) == true;

    public Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sink = sink;
        if (new StatFs(AppPaths.Library).AvailableBytes < MinFreeBytes)
        {
            throw new IOException("Spazio libero insufficiente per la registrazione audio (servono almeno 200 MB).");
        }

        _record = Open();

        var recording = new AudioRecording
        {
            Id = AudioRecording.NewId(),
            SessionId = sessionId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var path = Path.Combine(AppPaths.Library, recording.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new CafWriter(File.Create(path), AudioRecording.SampleRate, AudioRecording.Channels);

        _record.StartRecording();
        if (_record.RecordingState != RecordState.Recording)
        {
            throw new InvalidOperationException("Audio: il microfono è occupato da un'altra app.");
        }

        _running = true;
        _thread = new Thread(Pump) { Name = "csf.audio", IsBackground = true };
        _thread.Start();
        _recording = recording;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _running = false;
        try
        {
            // Sblocca la lettura in corso sul thread di registrazione.
            _record?.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Mai avviato: niente da fermare.
        }

        _thread?.Join();
        _thread = null;
        _record?.Release();
        _record?.Dispose();
        _record = null;

        // La chiusura scrive la dimensione definitiva nell'intestazione CAF.
        _file?.Dispose();
        _file = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _running = false;
        _record?.Release();
        _record?.Dispose();
        _record = null;
        _file?.Dispose();
        _file = null;
    }

    /// <summary>Metadati della registrazione completata (da chiamare dopo <see cref="StopAsync"/>).</summary>
    public AudioRecording? Complete()
    {
        if (_recording is null)
        {
            return null;
        }

        _recording.DurationSeconds = _framesWritten / (double)AudioRecording.SampleRate;
        _recording.Features = _features.Build();
        return _recording;
    }

    private static AudioRecord Open()
    {
        var context = global::Android.App.Application.Context;
        var unprocessed = (context.GetSystemService(Context.AudioService) as AudioManager)
            ?.GetProperty(AudioManager.PropertySupportAudioSourceUnprocessed) == "true";
        var minimum = AudioRecord.GetMinBufferSize(AudioRecording.SampleRate, ChannelIn.Mono, Encoding.PcmFloat);
        var bufferBytes = Math.Max(minimum, BufferFrames * sizeof(float) * 4);

        var record = new AudioRecord(unprocessed ? AudioSource.Unprocessed : AudioSource.VoiceRecognition,
            AudioRecording.SampleRate, ChannelIn.Mono, Encoding.PcmFloat, bufferBytes);
        if (record.State != State.Initialized)
        {
            record.Release();
            record.Dispose();
            throw new InvalidOperationException("Audio: il microfono non accetta la registrazione a 48 kHz in virgola mobile.");
        }

        return record;
    }

    private void Pump()
    {
        var buffer = new float[BufferFrames];
        while (_running && _record is { } record)
        {
            var read = record.Read(buffer, 0, buffer.Length, ReadBlocking);
            if (read <= 0)
            {
                if (_running && read < 0)
                {
                    logger.LogError("Lettura audio fallita (codice {Code}).", read);
                }

                break;
            }

            var samples = buffer.AsSpan(0, read);
            try
            {
                _file?.Write(samples);
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "Scrittura audio fallita.");
                break;
            }

            _framesWritten += read;
            var (rms, peak) = _features.Add(samples);
            // Istante del primo campione del buffer, come su iOS.
            var timestamp = clock.Now - (read / (double)AudioRecording.SampleRate);
            _sink?.TryWrite(new SensorReading(SensorKind.AudioLevel, timestamp, [rms, peak]));
        }
    }
}
