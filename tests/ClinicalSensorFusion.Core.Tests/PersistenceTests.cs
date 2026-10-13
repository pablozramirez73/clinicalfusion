using System.Security.Cryptography;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicalSensorFusion.Core.Tests;

internal sealed class FixedKeyProvider(byte[] key) : IDatabaseKeyProvider
{
    public Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult((byte[])key.Clone());
}

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "csf-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private string DbPath => Path.Combine(_dir, ClinicalDatabase.FileName);

    private ClinicalDatabase Open(byte[]? key = null) =>
        new(DbPath, new FixedKeyProvider(key ?? _key), NullLogger<ClinicalDatabase>.Instance);

    private static ClinicalSession NewSession() => new()
    {
        Id = ClinicalSession.NewId(),
        SubjectCode = "PZ-001",
        OperatorId = "op1",
        OperatorName = "Operatore Uno",
        Mode = CaptureMode.BodyLidar,
        StartedAt = DateTimeOffset.UtcNow,
        DeviceModel = "iPhone13,3",
    };

    [Fact]
    public async Task Database_file_is_encrypted_and_requires_the_right_key()
    {
        await using (var db = Open())
        {
            var repo = new ClinicalRepository(db);
            await repo.CreateSessionAsync(NewSession(), TestContext.Current.CancellationToken);
        }

        var header = new byte[16];
        await using (var f = File.OpenRead(DbPath))
        {
            await f.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
        }

        Assert.NotEqual("SQLite format 3\0"u8.ToArray(), header);

        await using var wrong = Open(RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<DatabaseKeyMismatchException>(() =>
            new ClinicalRepository(wrong).ListSessionsAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Session_round_trip_with_summary_chunks_audio_and_analysis()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Open();
        var repo = new ClinicalRepository(db);
        var session = NewSession();
        await repo.CreateSessionAsync(session, ct);

        var recorder = new SessionRecorder(session, repo, NullLogger<SessionRecorder>.Instance, flushSeconds: 0.5);
        recorder.Start();
        for (var i = 0; i < 300; i++)
        {
            var t = i / 100.0;
            Assert.True(recorder.TryWrite(new SensorReading(SensorKind.Motion, t, new float[16])));
            if (i % 10 == 0)
            {
                var angles = new float[JointAngleCalculator.JointCount + 2];
                Array.Fill(angles, float.NaN);
                angles[(int)Joint.LeftKneeFlexion] = 10 + i / 10f;
                angles[^2] = 0;
                angles[^1] = 2.1f;
                recorder.TryWrite(new SensorReading(SensorKind.JointAngles, t, angles));
            }
        }

        recorder.TryWrite(new SensorReading(SensorKind.Pedometer, 3, [12, 8.5f, 1.8f, 0, 0]));
        var audio = new AudioFeatures(3, -30, -6, 900, 0.5, 0);
        var summary = await recorder.CompleteAsync(3.0, audio);
        session.EndedAt = session.StartedAt.AddSeconds(3);
        session.Summary = summary;
        await repo.CompleteSessionAsync(session, ct);

        Assert.Equal(300, summary.SampleCounts[SensorKind.Motion]);
        Assert.Equal(10, summary.JointRanges[Joint.LeftKneeFlexion].Min, 1e-6);
        Assert.Equal(39, summary.JointRanges[Joint.LeftKneeFlexion].Max, 1e-6);
        Assert.Equal(1.0, summary.LidarCoverage);
        Assert.Equal(2.1, summary.MedianSubjectDistanceMeters!.Value, 1e-6);
        Assert.Equal(12, summary.Gait!.Steps);
        Assert.Equal(108, summary.Gait.CadenceStepsPerMinute!.Value, 1e-3);

        var motion = await repo.ReadStreamAsync(session.Id, SensorKind.Motion, ct);
        Assert.Equal(300, motion.Count);
        Assert.True(motion.Zip(motion.Skip(1)).All(p => p.First.Timestamp < p.Second.Timestamp));

        var recording = new AudioRecording { Id = AudioRecording.NewId(), SessionId = session.Id, CreatedAt = DateTimeOffset.UtcNow, DurationSeconds = 3, Features = audio };
        await repo.AddAudioRecordingAsync(recording, ct);
        Assert.Equal(32, recording.Id.Length);
        Assert.Equal(Path.Combine("audio", recording.Id, "audio.caf"), recording.RelativePath);

        await repo.AddAnalysisAsync(new AiAnalysis
        {
            Id = Guid.NewGuid().ToString("N"), SessionId = session.Id, CreatedAt = DateTimeOffset.UtcNow,
            Model = "gemma4:e4b-mlx", Language = "Italiano", OperatorId = "op1", Prompt = "p", Response = "r",
        }, ct);

        var loaded = await repo.GetSessionAsync(session.Id, ct);
        Assert.NotNull(loaded?.Summary);
        Assert.Equal(summary.JointRanges[Joint.LeftKneeFlexion], loaded.Summary.JointRanges[Joint.LeftKneeFlexion]);
        Assert.Single(await repo.ListAudioRecordingsAsync(session.Id, ct));
        Assert.Single(await repo.ListAnalysesAsync(session.Id, ct));
        Assert.Single(await repo.ListSessionsAsync("pz-0", ct: ct));
        Assert.Empty(await repo.ListSessionsAsync("%", ct: ct)); // il carattere jolly è trattato come testo

        await repo.DeleteSessionAsync(session.Id, "op1", ct);
        Assert.Empty(await repo.ReadStreamAsync(session.Id, SensorKind.Motion, ct));
        Assert.Empty(await repo.ListAnalysesAsync(session.Id, ct));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Schema del database del telefono: versione, aggiornamento da una versione precedente, rifiuto di una futura.</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "csf-migrations-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClinicalDatabase Open() =>
        new(Path.Combine(_dir, ClinicalDatabase.FileName), new FixedKeyProvider(_key), NullLogger<ClinicalDatabase>.Instance);

    private static Task<long> ScalarAsync(ClinicalDatabase db, string sql) => db.RunAsync(
        async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
        },
        Ct);

    private static Task ExecAsync(ClinicalDatabase db, string sql) =>
        db.RunAsync(connection => ClinicalDatabase.ExecAsync(connection, sql, Ct), Ct);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_new_database_is_at_the_latest_version_and_reopening_does_not_migrate_again()
    {
        await using (var db = Open())
        {
            Assert.Equal(Migrations.LatestVersion, await ScalarAsync(db, "PRAGMA user_version;"));
        }

        await using var reopened = Open();
        Assert.Equal(Migrations.LatestVersion, await ScalarAsync(reopened, "PRAGMA user_version;"));
        Assert.Empty(await new ClinicalRepository(reopened).ListUnsyncedSessionsAsync(Ct));
    }

    [Fact]
    public async Task A_version_1_database_is_upgraded_and_keeps_its_data()
    {
        string sessionId;
        await using (var db = Open())
        {
            var session = new ClinicalSession
            {
                Id = ClinicalSession.NewId(), SubjectCode = "PZ-V1", OperatorId = "op1", OperatorName = "Operatore Uno",
                Mode = CaptureMode.BodyLidar, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), EndedAt = DateTimeOffset.UtcNow,
            };
            sessionId = session.Id;
            var repo = new ClinicalRepository(db);
            await repo.CreateSessionAsync(session, Ct);
            await repo.CompleteSessionAsync(session, Ct);

            // Riporta lo schema alla v1 (prima della sincronizzazione): è l'inverso esatto del passo v2.
            await ExecAsync(db, "DROP INDEX ix_sessions_unsynced; ALTER TABLE sessions DROP COLUMN synced_at; PRAGMA user_version = 1;");
        }

        await using var upgraded = Open();
        Assert.Equal(Migrations.LatestVersion, await ScalarAsync(upgraded, "PRAGMA user_version;"));
        var pending = Assert.Single(await new ClinicalRepository(upgraded).ListUnsyncedSessionsAsync(Ct));
        Assert.Equal(sessionId, pending.Id);
        Assert.Equal("PZ-V1", pending.SubjectCode);
        Assert.Null(pending.SyncedAt);
    }

    [Fact]
    public async Task A_database_newer_than_the_app_is_refused_and_left_untouched()
    {
        var future = Migrations.LatestVersion + 1;
        await using (var db = Open())
        {
            await ExecAsync(db, $"PRAGMA user_version = {future};");
        }

        await using (var db = Open())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ClinicalRepository(db).ListSessionsAsync(ct: Ct));
            Assert.Contains("più recente", error.Message, StringComparison.Ordinal);
        }

        // Nessun downgrade silenzioso: la versione sul file è ancora quella futura.
        await using var raw = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScalarAsync(raw, "PRAGMA user_version;"));
    }
}

public class SensorChunkCodecTests
{
    [Fact]
    public void Round_trip_preserves_variable_length_samples()
    {
        var input = new List<SensorReading>
        {
            new(SensorKind.HandLandmarks, 0.1, [1, 2, 3]),
            new(SensorKind.HandLandmarks, 0.2, []),
            new(SensorKind.HandLandmarks, 0.3, [float.NaN, -1.5f]),
        };
        var output = SensorChunkCodec.Decode(SensorKind.HandLandmarks, SensorChunkCodec.Encode(input));
        Assert.Equal(3, output.Count);
        Assert.Equal(input[0].Values, output[0].Values);
        Assert.Empty(output[1].Values);
        Assert.True(float.IsNaN(output[2].Values[0]));
        Assert.Equal(0.3, output[2].Timestamp);
        Assert.Equal(3, SensorChunkCodec.CountSamples(SensorChunkCodec.Encode(input)));
    }

    [Fact]
    public void A_block_that_expands_beyond_the_limit_is_rejected_instead_of_filling_memory()
    {
        // ~260 KB di zeri: compressi sono poche decine di byte.
        var bomb = SensorChunkCodec.Encode([new SensorReading(SensorKind.Motion, 0, new float[65_535])]);
        Assert.True(bomb.Length < 1_000);

        Assert.Throws<InvalidDataException>(() => SensorChunkCodec.Decode(SensorKind.Motion, bomb, maxDecodedBytes: 100_000));
        Assert.Throws<InvalidDataException>(() => SensorChunkCodec.CountSamples(bomb, maxDecodedBytes: 100_000));
        Assert.Single(SensorChunkCodec.Decode(SensorKind.Motion, bomb));
    }

    [Fact]
    public void Truncated_or_foreign_data_is_reported_as_invalid()
    {
        static byte[] Brotli(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                brotli.Write(raw);
            }

            return output.ToArray();
        }

        // Versione corretta, poi un campione che dichiara 2 valori ma ne contiene uno solo.
        byte[] truncated = [1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 128, 63];
        Assert.Throws<InvalidDataException>(() => SensorChunkCodec.Decode(SensorKind.Motion, Brotli(truncated)));
        Assert.Throws<InvalidDataException>(() => SensorChunkCodec.CountSamples(Brotli([1, 0, 0])));
        Assert.Throws<InvalidDataException>(() => SensorChunkCodec.Decode(SensorKind.Motion, Brotli([9])));
        Assert.Throws<InvalidDataException>(() => SensorChunkCodec.Decode(SensorKind.Motion, [1, 2, 3, 4, 5]));
    }
}
