using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Persistence;

/// <summary>
/// Archivio clinico locale <c>clinical.db</c>, cifrato per intero (formato SQLCipher 4 tramite
/// SQLite3 Multiple Ciphers). Senza chiave il file è indistinguibile da rumore casuale.
/// </summary>
/// <remarks>
/// <para>Si usa una sola connessione serializzata da un semaforo: è un'app mobile mono-utente, il carico è
/// modesto e così si evitano sia la ripetizione della derivazione della chiave sia i problemi di pooling
/// con connessioni già cifrate. Pooling disattivato per lo stesso motivo.</para>
/// <para>La chiave è grezza (32 byte, sintassi <c>x'…'</c>): niente PBKDF2 all'apertura, perché è già
/// casuale ad alta entropia e non una password.</para>
/// </remarks>
public sealed class ClinicalDatabase : IAsyncDisposable
{
    public const string FileName = "clinical.db";

    private static int _batteriesInitialized;

    private readonly string _path;
    private readonly IDatabaseKeyProvider _keyProvider;
    private readonly ILogger<ClinicalDatabase> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public ClinicalDatabase(string path, IDatabaseKeyProvider keyProvider, ILogger<ClinicalDatabase> logger)
    {
        _path = path;
        _keyProvider = keyProvider;
        _logger = logger;
    }

    public string Path => _path;

    /// <summary>Esegue un'operazione con accesso esclusivo alla connessione (aperta e migrata al primo uso).</summary>
    public async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = _connection ??= await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await operation(connection).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken = default) =>
        RunAsync<bool>(async c => { await operation(c).ConfigureAwait(false); return true; }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _batteriesInitialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var key = await _keyProvider.GetOrCreateKeyAsync(ct).ConfigureAwait(false);
        if (key.Length != 32)
        {
            throw new InvalidOperationException("La chiave del database deve essere di 32 byte.");
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // Le PRAGMA di cifratura devono precedere qualsiasi altro accesso al file.
            // La chiave è esadecimale (solo [0-9A-F]): nessun rischio di injection nella stringa.
            await ExecAsync(connection, $"""
                PRAGMA cipher = 'sqlcipher';
                PRAGMA legacy = 4;
                PRAGMA key = "x'{Convert.ToHexString(key)}'";
                """, ct).ConfigureAwait(false);
            Array.Clear(key);

            // Verifica della chiave: con una chiave errata la prima lettura fallisce con SQLITE_NOTADB.
            await ExecAsync(connection, "SELECT count(*) FROM sqlite_master;", ct).ConfigureAwait(false);
            await ExecAsync(connection, """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                PRAGMA synchronous = NORMAL;
                """, ct).ConfigureAwait(false);

            await Migrations.ApplyAsync(connection, _logger, ct).ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26 /* SQLITE_NOTADB */)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new DatabaseKeyMismatchException(_path, ex);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task ExecAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Il file esiste ma non si decifra con la chiave disponibile (es. app reinstallata con un altro team di
/// firma: il Portachiavi non restituisce più la chiave originale). Non si sovrascrive mai il file.
/// </summary>
public sealed class DatabaseKeyMismatchException(string path, Exception inner)
    : Exception($"Impossibile decifrare '{path}': la chiave nel Portachiavi non corrisponde.", inner)
{
    public string DatabasePath { get; } = path;
}
