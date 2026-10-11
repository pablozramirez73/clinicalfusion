using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Persistence;

/// <summary>
/// Migrazioni dello schema, versionate con <c>PRAGMA user_version</c>. Ogni passo è in transazione:
/// o si applica tutto o niente. Le migrazioni sono solo additive; mai modificare un passo già rilasciato.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Steps =
    [
        // v1 — schema iniziale.
        """
        CREATE TABLE sessions (
            id            TEXT PRIMARY KEY,
            subject_code  TEXT NOT NULL,
            operator_id   TEXT NOT NULL,
            operator_name TEXT NOT NULL,
            mode          INTEGER NOT NULL,
            started_at    TEXT NOT NULL,
            ended_at      TEXT,
            notes         TEXT,
            device_model  TEXT,
            summary_json  TEXT
        );
        CREATE INDEX ix_sessions_started ON sessions(started_at DESC);
        CREATE INDEX ix_sessions_subject ON sessions(subject_code, started_at DESC);

        CREATE TABLE sensor_chunks (
            session_id    TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            kind          INTEGER NOT NULL,
            seq           INTEGER NOT NULL,
            t_start       REAL NOT NULL,
            t_end         REAL NOT NULL,
            sample_count  INTEGER NOT NULL,
            payload       BLOB NOT NULL,
            PRIMARY KEY (session_id, kind, seq)
        );

        CREATE TABLE audio_recordings (
            id            TEXT PRIMARY KEY,
            session_id    TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            created_at    TEXT NOT NULL,
            duration_s    REAL NOT NULL,
            features_json TEXT
        );
        CREATE INDEX ix_audio_session ON audio_recordings(session_id);

        CREATE TABLE ai_analyses (
            id            TEXT PRIMARY KEY,
            session_id    TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            created_at    TEXT NOT NULL,
            model         TEXT NOT NULL,
            language      TEXT NOT NULL,
            operator_id   TEXT NOT NULL,
            prompt        TEXT NOT NULL,
            response      TEXT NOT NULL
        );
        CREATE INDEX ix_analyses_session ON ai_analyses(session_id, created_at DESC);

        -- Tracciabilità: chi ha fatto cosa (dati sanitari).
        CREATE TABLE audit_log (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            at            TEXT NOT NULL,
            operator_id   TEXT NOT NULL,
            action        TEXT NOT NULL,
            entity_id     TEXT
        );
        """,
    ];

    public static int LatestVersion => Steps.Length;

    public static async Task ApplyAsync(SqliteConnection connection, ILogger logger, CancellationToken ct)
    {
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

        if (current > Steps.Length)
        {
            throw new InvalidOperationException(
                $"Il database è alla versione {current}, più recente di quella supportata ({Steps.Length}).");
        }

        for (var version = current + 1; version <= Steps.Length; version++)
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = Steps[version - 1] + $"\nPRAGMA user_version = {version};";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Schema del database aggiornato alla versione {Version}.", version);
        }
    }
}
