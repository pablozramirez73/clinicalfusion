using System.Threading.Channels;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using ClinicalSensorFusion.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Recording;

/// <summary>Persistenza dei blocchi di campioni.</summary>
public interface ISensorChunkStore
{
    Task AppendChunkAsync(string sessionId, SensorChunk chunk, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raccoglie i campioni di tutte le sorgenti (thread diversi: CoreMotion, ARKit, audio) e li serializza su
/// un unico consumer che aggiorna le metriche e scrive i blocchi nel DB.
/// </summary>
/// <remarks>
/// Scelte progettuali:
/// <list type="bullet">
/// <item>Canale limitato con <see cref="BoundedChannelFullMode.DropWrite"/>: i produttori sono callback di
/// sistema che non devono mai bloccarsi; se il disco rallenta si perdono campioni (contati e riportati nel
/// riepilogo come indice di qualità) invece di congelare camera e audio.</item>
/// <item>Singolo consumer: niente lock su aggregatore e buffer.</item>
/// <item>Blocchi chiusi a tempo (2 s) o a dimensione: in caso di crash si perdono al massimo pochi secondi.</item>
/// </list>
/// </remarks>
public sealed class SessionRecorder : ISensorSink, IAsyncDisposable
{
    private readonly ClinicalSession _session;
    private readonly ISensorChunkStore _store;
    private readonly ILogger _logger;
    private readonly Channel<SensorReading> _channel;
    private readonly SessionMetricsAggregator _aggregator;
    private readonly Dictionary<SensorKind, ChunkBuffer> _buffers = [];
    private readonly double _flushSeconds;
    private readonly int _maxSamplesPerChunk;
    private Task? _consumer;
    private long _dropped;
    private long _writeErrors;
    private volatile LiveMetrics? _live;
    private double _lastSnapshot = double.NegativeInfinity;

    public SessionRecorder(
        ClinicalSession session,
        ISensorChunkStore store,
        ILogger<SessionRecorder> logger,
        int capacity = 16_384,
        double flushSeconds = 2.0,
        int maxSamplesPerChunk = 2_048)
    {
        _session = session;
        _store = store;
        _logger = logger;
        _flushSeconds = flushSeconds;
        _maxSamplesPerChunk = maxSamplesPerChunk;
        _aggregator = new SessionMetricsAggregator(session.Mode);
        _channel = Channel.CreateBounded<SensorReading>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <summary>Ultime metriche (aggiornate ~10 volte al secondo). Null finché non arriva il primo campione.</summary>
    public LiveMetrics? Live => _live;

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    public bool TryWrite(in SensorReading reading)
    {
        if (_channel.Writer.TryWrite(reading))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        CsfTelemetry.Capture.DroppedSamples.Add(1, CsfTelemetry.Tag("kind", reading.Kind.ToString()));
        return false;
    }

    /// <summary>Chiude il canale, svuota i buffer e restituisce il riepilogo clinico.</summary>
    public async Task<SessionSummary> CompleteAsync(double durationSeconds, AudioFeatures? audio)
    {
        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }

        foreach (var buffer in _buffers.Values)
        {
            await FlushAsync(buffer).ConfigureAwait(false);
        }

        if (_writeErrors > 0)
        {
            _logger.LogWarning("Rilevazione {SessionId}: {Errors} blocchi non salvati.", _session.Id, _writeErrors);
        }

        var mode = CsfTelemetry.Tag("mode", _session.Mode.ToString());
        CsfTelemetry.Capture.Sessions.Add(1, mode);
        CsfTelemetry.Capture.SessionDuration.Record(durationSeconds, mode);
        if (DroppedSamples > 0)
        {
            _logger.LogWarning("Rilevazione {SessionId}: {Dropped} campioni scartati per saturazione del registratore.", _session.Id, DroppedSamples);
        }

        return _aggregator.Build(durationSeconds, audio, DroppedSamples);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            try
            {
                await _consumer.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore nella chiusura del registratore.");
            }
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (var reading in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _aggregator.Add(reading);

            if (!_buffers.TryGetValue(reading.Kind, out var buffer))
            {
                buffer = new ChunkBuffer(reading.Kind);
                _buffers[reading.Kind] = buffer;
            }

            buffer.Items.Add(reading);
            if (buffer.Items.Count >= _maxSamplesPerChunk || reading.Timestamp - buffer.Items[0].Timestamp >= _flushSeconds)
            {
                await FlushAsync(buffer).ConfigureAwait(false);
            }

            if (reading.Timestamp - _lastSnapshot >= 0.1)
            {
                _lastSnapshot = reading.Timestamp;
                _live = _aggregator.Snapshot();
            }
        }

        _live = _aggregator.Snapshot();
    }

    private async Task FlushAsync(ChunkBuffer buffer)
    {
        if (buffer.Items.Count == 0)
        {
            return;
        }

        var items = buffer.Items;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var kind = CsfTelemetry.Tag("kind", buffer.Kind.ToString());
        var chunk = new SensorChunk(
            buffer.Kind,
            buffer.NextSequence++,
            items[0].Timestamp,
            items[^1].Timestamp,
            items.Count,
            SensorChunkCodec.Encode(items));
        items.Clear();

        try
        {
            await _store.AppendChunkAsync(_session.Id, chunk).ConfigureAwait(false);
            CsfTelemetry.Capture.Samples.Add(chunk.SampleCount, kind);
            CsfTelemetry.Capture.ChunkSize.Record(chunk.Payload.Length, kind);
            CsfTelemetry.Capture.ChunkWriteDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, kind);
        }
        catch (Exception ex)
        {
            // Non si interrompe l'acquisizione per un errore di scrittura: si registra e si prosegue.
            _writeErrors++;
            CsfTelemetry.Capture.ChunkWriteErrors.Add(1, kind);
            _logger.LogError(ex, "Salvataggio del blocco {Kind}#{Seq} fallito.", chunk.Kind, chunk.Sequence);
        }
    }

    private sealed class ChunkBuffer(SensorKind kind)
    {
        public SensorKind Kind { get; } = kind;

        public List<SensorReading> Items { get; } = new(256);

        public int NextSequence { get; set; }
    }
}
