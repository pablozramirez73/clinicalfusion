using System.Globalization;
using System.Text.Json;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using Microsoft.Data.Sqlite;

namespace ClinicalSensorFusion.Core.Persistence;

/// <summary>
/// Accesso ai dati clinici con ADO.NET puro (Microsoft.Data.Sqlite). EF Core sarebbe più comodo ma su
/// iOS (AOT, trimming) aggiunge peso e richiede modelli precompilati; lo schema qui è piccolo e stabile.
/// Tutte le query sono parametrizzate.
/// </summary>
public sealed class ClinicalRepository(ClinicalDatabase database) : ISensorChunkStore
{
    private const string SessionColumns =
        "id, subject_code, operator_id, operator_name, mode, started_at, ended_at, notes, device_model, summary_json";

    public Task CreateSessionAsync(ClinicalSession s, CancellationToken ct = default) =>
        database.RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO sessions ({SessionColumns})
                VALUES ($id, $subject, $opId, $opName, $mode, $started, NULL, $notes, $device, NULL);
                """;
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.Parameters.AddWithValue("$subject", s.SubjectCode);
            cmd.Parameters.AddWithValue("$opId", s.OperatorId);
            cmd.Parameters.AddWithValue("$opName", s.OperatorName);
            cmd.Parameters.AddWithValue("$mode", (int)s.Mode);
            cmd.Parameters.AddWithValue("$started", Format(s.StartedAt));
            cmd.Parameters.AddWithValue("$notes", (object?)s.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$device", (object?)s.DeviceModel ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await AuditAsync(c, s.OperatorId, "session.create", s.Id, ct).ConfigureAwait(false);
        }, ct);

    public Task CompleteSessionAsync(ClinicalSession s, CancellationToken ct = default) =>
        database.RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE sessions SET ended_at = $ended, summary_json = $summary, notes = $notes, subject_code = $subject
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.Parameters.AddWithValue("$ended", s.EndedAt is { } e ? Format(e) : DBNull.Value);
            cmd.Parameters.AddWithValue("$summary", s.Summary is null
                ? DBNull.Value
                : JsonSerializer.Serialize(s.Summary, CsfJsonContext.Default.SessionSummary));
            cmd.Parameters.AddWithValue("$notes", (object?)s.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$subject", s.SubjectCode);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await AuditAsync(c, s.OperatorId, "session.complete", s.Id, ct).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<ClinicalSession>> ListSessionsAsync(string? subjectFilter = null, int limit = 200, CancellationToken ct = default) =>
        database.RunAsync<IReadOnlyList<ClinicalSession>>(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT {SessionColumns} FROM sessions
                WHERE $subject IS NULL OR subject_code LIKE $subject ESCAPE '\'
                ORDER BY started_at DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$subject", string.IsNullOrWhiteSpace(subjectFilter)
                ? DBNull.Value
                : "%" + EscapeLike(subjectFilter.Trim()) + "%");
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<ClinicalSession>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadSession(reader));
            }

            return list;
        }, ct);

    public Task<ClinicalSession?> GetSessionAsync(string id, CancellationToken ct = default) =>
        database.RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {SessionColumns} FROM sessions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadSession(reader) : null;
        }, ct);

    /// <summary>Elimina la rilevazione con blocchi, audio e analisi (cascade). I file audio sono a carico del chiamante.</summary>
    public Task DeleteSessionAsync(string id, string operatorId, CancellationToken ct = default) =>
        database.RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await AuditAsync(c, operatorId, "session.delete", id, ct).ConfigureAwait(false);
        }, ct);

    public Task AppendChunkAsync(string sessionId, SensorChunk chunk, CancellationToken cancellationToken = default) =>
        database.RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sensor_chunks (session_id, kind, seq, t_start, t_end, sample_count, payload)
                VALUES ($session, $kind, $seq, $t0, $t1, $count, $payload);
                """;
            cmd.Parameters.AddWithValue("$session", sessionId);
            cmd.Parameters.AddWithValue("$kind", (int)chunk.Kind);
            cmd.Parameters.AddWithValue("$seq", chunk.Sequence);
            cmd.Parameters.AddWithValue("$t0", chunk.StartTime);
            cmd.Parameters.AddWithValue("$t1", chunk.EndTime);
            cmd.Parameters.AddWithValue("$count", chunk.SampleCount);
            cmd.Parameters.Add("$payload", SqliteType.Blob).Value = chunk.Payload;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Legge e decodifica tutti i campioni di un flusso, in ordine temporale.</summary>
    public Task<IReadOnlyList<SensorReading>> ReadStreamAsync(string sessionId, SensorKind kind, CancellationToken ct = default) =>
        database.RunAsync<IReadOnlyList<SensorReading>>(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT payload FROM sensor_chunks WHERE session_id = $session AND kind = $kind ORDER BY seq;";
            cmd.Parameters.AddWithValue("$session", sessionId);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            var result = new List<SensorReading>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.AddRange(SensorChunkCodec.Decode(kind, (byte[])reader[0]));
            }

            return result;
        }, ct);

    public Task AddAudioRecordingAsync(AudioRecording a, CancellationToken ct = default) =>
        database.RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audio_recordings (id, session_id, created_at, duration_s, features_json)
                VALUES ($id, $session, $created, $duration, $features);
                """;
            cmd.Parameters.AddWithValue("$id", a.Id);
            cmd.Parameters.AddWithValue("$session", a.SessionId);
            cmd.Parameters.AddWithValue("$created", Format(a.CreatedAt));
            cmd.Parameters.AddWithValue("$duration", a.DurationSeconds);
            cmd.Parameters.AddWithValue("$features", a.Features is null
                ? DBNull.Value
                : JsonSerializer.Serialize(a.Features, CsfJsonContext.Default.AudioFeatures));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<AudioRecording>> ListAudioRecordingsAsync(string sessionId, CancellationToken ct = default) =>
        database.RunAsync<IReadOnlyList<AudioRecording>>(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, created_at, duration_s, features_json FROM audio_recordings
                WHERE session_id = $session ORDER BY created_at;
                """;
            cmd.Parameters.AddWithValue("$session", sessionId);
            var list = new List<AudioRecording>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new AudioRecording
                {
                    Id = reader.GetString(0),
                    SessionId = reader.GetString(1),
                    CreatedAt = Parse(reader.GetString(2)),
                    DurationSeconds = reader.GetDouble(3),
                    Features = reader.IsDBNull(4)
                        ? null
                        : JsonSerializer.Deserialize(reader.GetString(4), CsfJsonContext.Default.AudioFeatures),
                });
            }

            return list;
        }, ct);

    public Task AddAnalysisAsync(AiAnalysis a, CancellationToken ct = default) =>
        database.RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ai_analyses (id, session_id, created_at, model, language, operator_id, prompt, response)
                VALUES ($id, $session, $created, $model, $language, $operator, $prompt, $response);
                """;
            cmd.Parameters.AddWithValue("$id", a.Id);
            cmd.Parameters.AddWithValue("$session", a.SessionId);
            cmd.Parameters.AddWithValue("$created", Format(a.CreatedAt));
            cmd.Parameters.AddWithValue("$model", a.Model);
            cmd.Parameters.AddWithValue("$language", a.Language);
            cmd.Parameters.AddWithValue("$operator", a.OperatorId);
            cmd.Parameters.AddWithValue("$prompt", a.Prompt);
            cmd.Parameters.AddWithValue("$response", a.Response);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await AuditAsync(c, a.OperatorId, "analysis.create", a.SessionId, ct).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<AiAnalysis>> ListAnalysesAsync(string sessionId, CancellationToken ct = default) =>
        database.RunAsync<IReadOnlyList<AiAnalysis>>(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, created_at, model, language, operator_id, prompt, response
                FROM ai_analyses WHERE session_id = $session ORDER BY created_at DESC;
                """;
            cmd.Parameters.AddWithValue("$session", sessionId);
            var list = new List<AiAnalysis>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new AiAnalysis
                {
                    Id = reader.GetString(0),
                    SessionId = reader.GetString(1),
                    CreatedAt = Parse(reader.GetString(2)),
                    Model = reader.GetString(3),
                    Language = reader.GetString(4),
                    OperatorId = reader.GetString(5),
                    Prompt = reader.GetString(6),
                    Response = reader.GetString(7),
                });
            }

            return list;
        }, ct);

    private static ClinicalSession ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        SubjectCode = r.GetString(1),
        OperatorId = r.GetString(2),
        OperatorName = r.GetString(3),
        Mode = (CaptureMode)r.GetInt32(4),
        StartedAt = Parse(r.GetString(5)),
        EndedAt = r.IsDBNull(6) ? null : Parse(r.GetString(6)),
        Notes = r.IsDBNull(7) ? null : r.GetString(7),
        DeviceModel = r.IsDBNull(8) ? null : r.GetString(8),
        Summary = r.IsDBNull(9) ? null : JsonSerializer.Deserialize(r.GetString(9), CsfJsonContext.Default.SessionSummary),
    };

    private static async Task AuditAsync(SqliteConnection c, string operatorId, string action, string? entityId, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_log (at, operator_id, action, entity_id) VALUES ($at, $op, $action, $entity);";
        cmd.Parameters.AddWithValue("$at", Format(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$op", operatorId);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$entity", (object?)entityId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
