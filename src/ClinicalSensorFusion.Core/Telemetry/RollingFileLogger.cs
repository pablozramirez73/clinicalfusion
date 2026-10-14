using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Telemetry;

public sealed class RollingFileLoggerOptions
{
    /// <summary>Cartella dei file di log (creata se manca).</summary>
    public required string Directory { get; init; }

    public string FilePrefix { get; init; } = "csf";

    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    /// <summary>Oltre questa dimensione si apre un nuovo file.</summary>
    public long MaxFileBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>File conservati: i più vecchi si cancellano. Lo spazio occupato resta ≤ MaxFiles × MaxFileBytes.</summary>
    public int MaxFiles { get; init; } = 5;

    /// <summary>Righe in attesa di scrittura: oltre, si scartano (e si contano) invece di bloccare chi scrive.</summary>
    public int QueueCapacity { get; init; } = 4_096;
}

/// <summary>
/// Log strutturato su file (una riga JSON per evento), con rotazione a dimensione e spazio limitato.
/// Sul telefono è l'unica destinazione dei log nelle build di rilascio: resta nel contenitore dell'app e
/// l'operatore lo condivide solo se lo decide.
/// </summary>
/// <remarks>
/// La scrittura è su un thread in background con coda limitata: un log non rallenta mai l'acquisizione.
/// Ogni riga ha <c>ts</c>, <c>level</c>, <c>category</c>, <c>message</c> e, se presenti, <c>eventId</c>,
/// <c>traceId</c>, <c>exception</c>, <c>scope</c> e i parametri del messaggio in <c>fields</c>.
/// I parametri si scrivono così come sono: chi scrive un log non deve passarvi dati sanitari
/// (vedi <see cref="LogRedaction"/>).
/// </remarks>
public sealed class RollingFileLoggerProvider : ILoggerProvider, ISupportExternalScope, IAsyncDisposable
{
    private readonly RollingFileLoggerOptions _options;
    private readonly Channel<byte[]> _queue;
    private readonly Task _writer;
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private long _dropped;
    private int _disposed;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        _options = options;
        System.IO.Directory.CreateDirectory(options.Directory);
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>File correnti, dal più recente.</summary>
    public IReadOnlyList<string> Files => ListFiles(_options);

    public static IReadOnlyList<string> ListFiles(RollingFileLoggerOptions options) =>
        System.IO.Directory.Exists(options.Directory)
            ? [.. System.IO.Directory.GetFiles(options.Directory, options.FilePrefix + "-*.jsonl").OrderByDescending(f => f, StringComparer.Ordinal)]
            : [];

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    /// <summary>Attende che le righe accodate finora siano su disco (per i test e prima di condividere i file).</summary>
    public async Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _flushRequests.Enqueue(done);
        // Riga vuota = solo segnale. Se la coda è piena il segnale non parte, ma chi scrive svuota comunque
        // le richieste appena la coda si esaurisce.
        _queue.Writer.TryWrite([]);
        await Task.WhenAny(done.Task, _writer).ConfigureAwait(false);
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource> _flushRequests = new();

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private void Enqueue(byte[] line)
    {
        if (!_queue.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task WriteLoopAsync()
    {
        FileStream? file = null;
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (line.Length > 0)
                    {
                        file = Roll(file, line.Length);
                        await file.WriteAsync(line).ConfigureAwait(false);

                        var dropped = Interlocked.Exchange(ref _dropped, 0);
                        if (dropped > 0)
                        {
                            await file.WriteAsync(Encoding.UTF8.GetBytes(
                                $$"""{"ts":"{{DateTimeOffset.UtcNow:O}}","level":"Warning","category":"{{nameof(RollingFileLoggerProvider)}}","message":"{{dropped}} righe di log scartate: coda piena."}""" + "\n")).ConfigureAwait(false);
                        }
                    }

                    if (_queue.Reader.Count == 0 && file is not null)
                    {
                        await file.FlushAsync().ConfigureAwait(false);
                        while (_flushRequests.TryDequeue(out var request))
                        {
                            request.TrySetResult();
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Disco pieno o file non scrivibile: il log non deve mai far cadere l'app.
                    Debug.WriteLine($"Log su file non riuscito: {ex.Message}");
                    file?.Dispose();
                    file = null;
                }
            }
        }
        finally
        {
            file?.Dispose();
            while (_flushRequests.TryDequeue(out var request))
            {
                request.TrySetResult();
            }
        }
    }

    private FileStream Roll(FileStream? current, int incoming)
    {
        if (current is not null && current.Length + incoming <= _options.MaxFileBytes)
        {
            return current;
        }

        current?.Dispose();

        // Nome ordinabile; il suffisso evita collisioni se si ruota due volte nello stesso millisecondo.
        var path = Path.Combine(_options.Directory, $"{_options.FilePrefix}-{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}.jsonl");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16_384, useAsync: true);
        foreach (var old in ListFiles(_options).Skip(Math.Max(1, _options.MaxFiles)))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Vecchio log non eliminato: {ex.Message}");
            }
        }

        return stream;
    }

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._options.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || provider._disposed != 0)
            {
                return;
            }

            using var buffer = new MemoryStream(256);
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("ts", DateTimeOffset.UtcNow);
                json.WriteString("level", logLevel.ToString());
                json.WriteString("category", category);
                if (eventId.Id != 0)
                {
                    json.WriteNumber("eventId", eventId.Id);
                }

                json.WriteString("message", formatter(state, exception));
                if (Activity.Current is { } activity)
                {
                    json.WriteString("traceId", activity.TraceId.ToString());
                }

                if (state is IReadOnlyList<KeyValuePair<string, object?>> fields && fields.Count > 1)
                {
                    json.WriteStartObject("fields");
                    WriteFields(json, fields);
                    json.WriteEndObject();
                }

                var scopeOpen = false;
                provider._scopes.ForEachScope(
                    (scope, writer) =>
                    {
                        if (!scopeOpen)
                        {
                            writer.WriteStartObject("scope");
                            scopeOpen = true;
                        }

                        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        {
                            WriteFields(writer, pairs);
                        }
                        else if (scope is not null)
                        {
                            writer.WriteString("scope", scope.ToString());
                        }
                    },
                    json);
                if (scopeOpen)
                {
                    json.WriteEndObject();
                }

                if (exception is not null)
                {
                    json.WriteString("exception", exception.ToString());
                }

                json.WriteEndObject();
            }

            buffer.WriteByte((byte)'\n');
            provider.Enqueue(buffer.ToArray());
        }

        private static void WriteFields(Utf8JsonWriter json, IEnumerable<KeyValuePair<string, object?>> fields)
        {
            foreach (var (key, value) in fields)
            {
                if (key == "{OriginalFormat}")
                {
                    continue;
                }

                switch (value)
                {
                    case null:
                        json.WriteNull(key);
                        break;
                    case bool b:
                        json.WriteBoolean(key, b);
                        break;
                    case int or long or short or byte:
                        json.WriteNumber(key, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case double d when double.IsFinite(d):
                        json.WriteNumber(key, d);
                        break;
                    case float f when float.IsFinite(f):
                        json.WriteNumber(key, f);
                        break;
                    default:
                        json.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                }
            }
        }
    }
}
