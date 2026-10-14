using ClinicalSensorFusion.Core.Localization;
using System.Runtime.InteropServices;
using AudioToolbox;
using AVFoundation;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using Foundation;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Platforms.iOS.Sensors;

/// <summary>
/// Microfono → <c>Library/audio/&lt;id&gt;/audio.caf</c>, CAF mono 48 kHz Float32 non compresso
/// (stesso formato dell'app originale, ≈11,5 MB/min).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Modalità <c>AVAudioSessionModeMeasurement</c>: disattiva AGC, riduzione del rumore ed equalizzazione
/// di iOS. Per un'analisi acustica clinica serve il segnale "vero", non quello ottimizzato per le chiamate.</item>
/// <item>Float32 non compresso: nessun artefatto di codifica, dinamica piena per l'analisi successiva.</item>
/// <item>Gli indicatori (RMS, picco, attività, clipping) si calcolano sui buffer in arrivo: a fine
/// registrazione sono già pronti senza rileggere il file.</item>
/// </list>
/// </remarks>
internal sealed class AudioCaptureSource(SensorClock clock, string sessionId, ILogger logger) : ISensorSource
{
    private const long MinFreeBytes = 200L * 1024 * 1024;

    private readonly AudioFeatureAccumulator _features = new(AudioRecording.SampleRate);
    private readonly AVAudioFormat _target = new(AVAudioCommonFormat.PCMFloat32, AudioRecording.SampleRate, AudioRecording.Channels, interleaved: false);
    private readonly Lock _gate = new();
    private AVAudioEngine? _engine;
    private AVAudioFile? _file;
    private AVAudioConverter? _converter;
    private AudioRecording? _recording;
    private ISensorSink? _sink;
    private long _framesWritten;

    public string DisplayName => Loc.T("Microfono");

    public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.AudioLevel];

    public bool IsAvailable => AVAudioSession.SharedInstance().InputAvailable;

    public Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sink = sink;
        EnsureFreeSpace();

        var session = AVAudioSession.SharedInstance();
        Check(session.SetCategory(AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetoothA2DP), "categoria");
        session.SetMode(AVAudioSessionMode.Measurement, out var modeError);
        Check(modeError, Loc.T("modalità misura"));
        session.SetPreferredSampleRate(AudioRecording.SampleRate, out _);
        session.SetActive(true, out var activeError);
        Check(activeError, "attivazione");

        var recording = new AudioRecording
        {
            Id = AudioRecording.NewId(),
            SessionId = sessionId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var path = Path.Combine(AppPaths.Library, recording.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var settings = new AudioSettings
        {
            Format = AudioFormatType.LinearPCM,
            SampleRate = AudioRecording.SampleRate,
            NumberChannels = AudioRecording.Channels,
            LinearPcmBitDepth = 32,
            LinearPcmFloat = true,
            LinearPcmBigEndian = false,
            LinearPcmNonInterleaved = false,
        };
        _file = new AVAudioFile(NSUrl.FromFilename(path), settings, AVAudioCommonFormat.PCMFloat32, interleaved: false, out var fileError);
        Check(fileError, Loc.T("creazione file"));

        // Protezione dati iOS: file cifrato a dispositivo bloccato, ma scrivibile se già aperto.
        NSFileManager.DefaultManager.SetAttributes(new NSFileAttributes { ProtectionKey = NSFileProtection.CompleteUnlessOpen }, path, out _);

        _engine = new AVAudioEngine();
        var input = _engine.InputNode;
        var inputFormat = input.GetBusOutputFormat(0);
        if (Math.Abs(inputFormat.SampleRate - AudioRecording.SampleRate) > 0.5 || inputFormat.ChannelCount != AudioRecording.Channels)
        {
            _converter = new AVAudioConverter(inputFormat, _target);
        }

        input.InstallTapOnBus(0, 4096, inputFormat, OnBuffer);
        _engine.Prepare();
        if (!_engine.StartAndReturnError(out var startError))
        {
            Check(startError, Loc.T("avvio motore audio"));
        }

        _recording = recording;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_engine is not null)
        {
            _engine.InputNode.RemoveTapOnBus(0);
            _engine.Stop();
            _engine.Dispose();
            _engine = null;
        }

        lock (_gate)
        {
            // Il rilascio dell'AVAudioFile chiude il file e aggiorna l'intestazione CAF.
            _file?.Dispose();
            _file = null;
            _converter?.Dispose();
            _converter = null;
        }

        AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _file?.Dispose();
        _converter?.Dispose();
        _target.Dispose();
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

    private void OnBuffer(AVAudioPcmBuffer buffer, AVAudioTime when)
    {
        lock (_gate)
        {
            if (_file is null)
            {
                return;
            }

            var output = buffer;
            if (_converter is not null)
            {
                var capacity = (uint)Math.Ceiling(buffer.FrameLength * AudioRecording.SampleRate / buffer.Format.SampleRate) + 64;
                output = new AVAudioPcmBuffer(_target, capacity);
                var consumed = false;
                _converter.ConvertToBuffer(output, out var convertError, (uint _, out AVAudioConverterInputStatus status) =>
                {
                    if (consumed)
                    {
                        status = AVAudioConverterInputStatus.NoDataNow;
                        return null!;
                    }

                    consumed = true;
                    status = AVAudioConverterInputStatus.HaveData;
                    return buffer;
                });

                if (convertError is not null)
                {
                    logger.LogWarning("Conversione audio fallita: {Error}", convertError.LocalizedDescription);
                    return;
                }
            }

            if (!_file.WriteFromBuffer(output, out var writeError))
            {
                logger.LogError("Scrittura audio fallita: {Error}", writeError?.LocalizedDescription);
                return;
            }

            _framesWritten += output.FrameLength;
            var (rms, peak) = _features.Add(Samples(output));
            var timestamp = when.HostTimeValid ? clock.FromUptime(AVAudioTime.SecondsForHostTime(when.HostTime)) : clock.Now;
            _sink?.TryWrite(new SensorReading(SensorKind.AudioLevel, timestamp, [rms, peak]));

            if (!ReferenceEquals(output, buffer))
            {
                output.Dispose();
            }
        }
    }

    private static unsafe ReadOnlySpan<float> Samples(AVAudioPcmBuffer buffer)
    {
        // FloatChannelData è un float** (un puntatore per canale, formato non interleaved).
        var channel0 = Marshal.ReadIntPtr(buffer.FloatChannelData);
        return new ReadOnlySpan<float>((void*)channel0, (int)buffer.FrameLength);
    }

    private static void EnsureFreeSpace()
    {
        var attributes = NSFileManager.DefaultManager.GetFileSystemAttributes(AppPaths.Library, out _);
        if (attributes is not null && (long)attributes.FreeSize < MinFreeBytes)
        {
            throw new IOException(Loc.T("Spazio libero insufficiente per la registrazione audio (servono almeno 200 MB)."));
        }
    }

    private static void Check(NSError? error, string step)
    {
        if (error is not null)
        {
            throw new InvalidOperationException($"Audio ({step}): {error.LocalizedDescription}");
        }
    }
}
