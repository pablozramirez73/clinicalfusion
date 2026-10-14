using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Server.Endpoints;
using ClinicalSensorFusion.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicalSensorFusion.Server.Tests;

/// <summary>
/// Percorso completo: archivio cifrato del telefono (SQLite3MC) → SessionSynchronizer → API di ingestione →
/// PostgreSQL → API della dashboard (anagrafica, replay 3D, spettrogramma, audio WAV).
/// </summary>
public sealed class SyncEndToEndTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncDisposable
{
    private readonly string _phoneDir = Path.Combine(Path.GetTempPath(), "csf-phone-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class KeyProvider : IDatabaseKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

        public Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult((byte[])_key.Clone());
    }

    [Fact]
    public async Task Phone_sessions_reach_the_dashboard_and_resync_is_idempotent()
    {
        await using var phoneDb = new ClinicalDatabase(Path.Combine(_phoneDir, "clinical.db"), new KeyProvider(), NullLogger<ClinicalDatabase>.Instance);
        var repo = new ClinicalRepository(phoneDb);
        var (session, audio) = await RecordSessionOnPhoneAsync(repo);

        var sync = new SessionSynchronizer(repo, new ClinicalSyncClient(server.CreateDeviceClient()), NullLogger<SessionSynchronizer>.Instance)
        {
            MaxBatchBytes = 20_000, // forza più lotti
        };
        var server_ = server.CreateHttpsClient().BaseAddress!;
        var report = await sync.SyncPendingAsync(server_, a => Path.Combine(_phoneDir, a.RelativePath), Ct);
        Assert.Empty(report.Errors);
        Assert.Equal(1, report.Sessions);
        Assert.Equal(1, report.AudioFiles);
        Assert.True(report.Chunks > 1);
        Assert.Empty(await repo.ListUnsyncedSessionsAsync(Ct));

        // Dashboard: il codice sconosciuto ha creato un'anagrafica segnaposto. Il database è condiviso con gli
        // altri test della classe, che possono crearne altre: si cerca quella di questo codice, che dev'essere una.
        var clinician = await server.CreateClinicianClientAsync();
        var patients = await clinician.GetFromJsonAsync<PagedResult<PatientListItem>>("api/v1/patients?placeholdersOnly=true", Ct);
        var patient = Assert.Single(patients!.Items, p => p.Code == "PZ-E2E");
        Assert.Equal("PZ-E2E", patient.Code);
        Assert.Equal(1, patient.SessionCount);

        var detail = (await clinician.GetFromJsonAsync<SessionDetail>($"api/v1/sessions/{session.Id}", Ct))!;
        Assert.Equal(CaptureMode.BodyLidar, detail.Mode);
        Assert.Contains(detail.Report, r => r.Label == "Flessione ginocchio dx");
        Assert.Contains(detail.Report, r => r is { Section: "Sensori ambientali", Label: "GPS · Posizione", Value: "45.46421, 9.18998" });
        Assert.Contains(detail.Report, r => r.Label == "Luce ambientale · Intensità" && r.Value.StartsWith("400–440", StringComparison.Ordinal));
        Assert.Equal(150, detail.Streams[SensorKind.PoseLandmarks3D]);
        Assert.Equal(1000, detail.Streams[SensorKind.Motion]);
        Assert.True(Assert.Single(detail.Audio).Available);
        Assert.Single(detail.Analyses);

        var pose = (await clinician.GetFromJsonAsync<PoseReplay>($"api/v1/sessions/{session.Id}/pose?maxFrames=50", Ct))!;
        Assert.Equal(50, pose.FrameCount);
        Assert.Equal(PoseLandmark.Count * 3, pose.Frames[0].Points.Length);

        var spectrogram = (await clinician.GetFromJsonAsync<Spectrogram>($"api/v1/sessions/{session.Id}/spectrogram?window=256&columns=20", Ct))!;
        Assert.NotEmpty(spectrogram.Times);
        // Il picco del primo spettro deve essere vicino ai 6 Hz del tremore simulato.
        var column = spectrogram.PowerDb[0];
        var peak = spectrogram.Frequencies[Array.IndexOf(column, column.Max())];
        Assert.InRange(peak, 5.0, 7.0);

        var trends = (await clinician.GetFromJsonAsync<List<TrendPoint>>($"api/v1/patients/{patient.Id}/trends", Ct))!;
        Assert.True(Assert.Single(trends).Rom[Joint.RightKneeFlexion].Max > 80);

        var wav = await clinician.GetAsync($"api/v1/sessions/{session.Id}/audio/{audio.Id}.wav", Ct);
        Assert.Equal("audio/wav", wav.Content.Headers.ContentType!.MediaType);
        var bytes = await wav.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal(44 + 4800 * 4, bytes.Length);
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(44 + 4 * 10)), 1e-6f);

        // Reinvio completo (es. app reinstallata): nessun duplicato.
        await repo.MarkSyncedAsync(session.Id, DateTimeOffset.MinValue, Ct);
        await using (var cmd = (await OpenRawAsync(phoneDb)))
        {
        }

        var direct = new ClinicalSyncClient(server.CreateDeviceClient());
        var chunks = new List<ChunkUpload>();
        await foreach (var c in repo.ReadRawChunksAsync(session.Id, Ct))
        {
            chunks.Add(new ChunkUpload(c.Kind, c.Sequence, c.StartTime, c.EndTime, c.SampleCount, c.Payload));
        }

        var again = await direct.UploadChunksAsync(server_, session.Id, chunks, Ct);
        Assert.Equal(chunks.Count, again.Received);
        Assert.Equal(0, again.Inserted);
        var detailAgain = (await clinician.GetFromJsonAsync<SessionDetail>($"api/v1/sessions/{session.Id}", Ct))!;
        Assert.Equal(1000, detailAgain.Streams[SensorKind.Motion]);

        // Overview della dashboard.
        var overview = (await clinician.GetFromJsonAsync<Overview>("api/v1/dashboard/overview", Ct))!;
        Assert.Equal(1, overview.Sessions);
        Assert.Equal(1, overview.PlaceholderPatients);
        Assert.Contains(overview.Tremor, t => t.SessionId == session.Id);
    }

    [Fact]
    public async Task Audio_must_be_a_caf_and_belong_to_a_known_session()
    {
        var device = server.CreateDeviceClient();
        var unknown = await device.PutAsync($"api/v1/ingest/sessions/{Guid.NewGuid():N}/audio/{Guid.NewGuid():N}", new ByteArrayContent(Caf(10)), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var traversal = await device.PutAsync("api/v1/ingest/sessions/..%2F..%2Fetc/audio/passwd", new ByteArrayContent(Caf(10)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
    }

    private static Task<IAsyncDisposable> OpenRawAsync(ClinicalDatabase db) => Task.FromResult<IAsyncDisposable>(db);

    private async Task<(ClinicalSession Session, AudioRecording Audio)> RecordSessionOnPhoneAsync(ClinicalRepository repo)
    {
        var session = new ClinicalSession
        {
            Id = ClinicalSession.NewId(), SubjectCode = "pz-e2e", OperatorId = "OP1", OperatorName = "Operatore",
            Mode = CaptureMode.BodyLidar, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), DeviceModel = "iPhone13,3",
        };
        await repo.CreateSessionAsync(session, Ct);

        var recorder = new SessionRecorder(session, repo, NullLogger<SessionRecorder>.Instance, flushSeconds: 1);
        recorder.Start();
        var rng = new Random(1);
        for (var i = 0; i < 150; i++)
        {
            var t = i / 15.0;
            var knee = (float)(90 * 0.5 * (1 - Math.Cos(2 * Math.PI * t / 5)));
            var pose = new float[PoseLandmark.Count * 3];
            for (var j = 0; j < pose.Length; j++)
            {
                pose[j] = (float)rng.NextDouble();
            }

            var angles = Enumerable.Repeat(float.NaN, JointAngleCalculator.JointCount + 2).ToArray();
            angles[(int)Joint.RightKneeFlexion] = knee;
            angles[^2] = 0;
            angles[^1] = 2.4f;
            recorder.TryWrite(new SensorReading(SensorKind.PoseLandmarks3D, t, pose));
            recorder.TryWrite(new SensorReading(SensorKind.JointAngles, t, angles));
        }

        for (var i = 0; i < 1000; i++)
        {
            var t = i / 100.0;
            var tremor = (float)(0.03 * Math.Sin(2 * Math.PI * 6 * t));
            recorder.TryWrite(new SensorReading(SensorKind.Motion, t, [tremor, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0]));
        }

        // Sensori ambientali accesi durante la rilevazione.
        recorder.TryWrite(new SensorReading(SensorKind.Location, 1, [45.46421f, 9.18998f, 120f, 12f, 0f]));
        for (var i = 0; i < 5; i++)
        {
            recorder.TryWrite(new SensorReading(SensorKind.AmbientLight, i, [400 + (i * 10), 4800]));
        }

        var features = new AudioFeatures(0.1, -20, -12, 100, 0.5, 0);
        session.Summary = await recorder.CompleteAsync(10, features);
        session.EndedAt = session.StartedAt.AddSeconds(10);
        await repo.CompleteSessionAsync(session, Ct);

        var audio = new AudioRecording { Id = AudioRecording.NewId(), SessionId = session.Id, CreatedAt = DateTimeOffset.UtcNow, DurationSeconds = 0.1, Features = features };
        var path = Path.Combine(_phoneDir, audio.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Caf(4800), Ct);
        await repo.AddAudioRecordingAsync(audio, Ct);

        await repo.AddAnalysisAsync(new AiAnalysis
        {
            Id = Guid.NewGuid().ToString("N"), SessionId = session.Id, CreatedAt = DateTimeOffset.UtcNow,
            Model = "gemma4:e4b-mlx", Language = "Italiano", OperatorId = "OP1", Prompt = "p", Response = "Sintesi di prova",
        }, Ct);

        return (session, audio);
    }

    [Fact]
    public async Task Audio_must_be_a_readable_caf_not_just_its_magic_bytes()
    {
        var device = server.CreateDeviceClient();
        var sessionId = Guid.NewGuid().ToString("N");
        var audioId = Guid.NewGuid().ToString("N");
        var start = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        var upload = new SessionUpload(sessionId, "pz-caf", "OP1", "Operatrice", CaptureMode.BodyLidar, start, start.AddMinutes(1),
            null, null, new SessionSummary { DurationSeconds = 60 }, [new AudioUpload(audioId, start, 1, null)], []);
        (await device.PutAsJsonAsync($"api/v1/ingest/sessions/{sessionId}", upload, SyncJsonContext.Default.SessionUpload, Ct)).EnsureSuccessStatusCode();

        var path = $"api/v1/ingest/sessions/{sessionId}/audio/{audioId}";
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PutAsync(path, new ByteArrayContent("caff"u8.ToArray()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.PutAsync(path, new ByteArrayContent(Caf(10)), Ct)).StatusCode);
    }

    /// <summary>CAF minimo come quello di AVAudioFile: lpcm Float32 little endian, mono 48 kHz.</summary>
    internal static byte[] Caf(int frames)
    {
        var data = new byte[8 + 12 + 32 + 12 + 4 + frames * 4];
        var s = data.AsSpan();
        "caff"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 1);
        var o = 8;
        "desc"u8.CopyTo(s[o..]);
        BinaryPrimitives.WriteInt64BigEndian(s[(o + 4)..], 32);
        o += 12;
        BinaryPrimitives.WriteDoubleBigEndian(s[o..], 48000);
        "lpcm"u8.CopyTo(s[(o + 8)..]);
        BinaryPrimitives.WriteUInt32BigEndian(s[(o + 12)..], 1 | 2);
        BinaryPrimitives.WriteUInt32BigEndian(s[(o + 16)..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(s[(o + 20)..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(s[(o + 24)..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(s[(o + 28)..], 32);
        o += 32;
        "data"u8.CopyTo(s[o..]);
        BinaryPrimitives.WriteInt64BigEndian(s[(o + 4)..], 4 + frames * 4L);
        o += 16; // header (12) + edit count (4)
        for (var i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(s[(o + i * 4)..], i == 10 ? 0.25f : 0f);
        }

        return data;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_phoneDir, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class DemoSeedTests : IAsyncLifetime
{
    private readonly ServerFixture _server = new() { SeedDemo = true };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task Demo_data_populates_the_overview_and_3d_endpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await _server.CreateClinicianClientAsync();
        var overview = (await client.GetFromJsonAsync<Overview>("api/v1/dashboard/overview", ct))!;
        Assert.Equal(6, overview.Patients);
        Assert.True(overview.Sessions >= 24);
        Assert.NotEmpty(overview.Weekly);
        Assert.NotEmpty(overview.JointAverages);
        Assert.Contains(overview.Tremor, t => t.FrequencyHz is > 4 and < 5.5); // paziente demo con Parkinson

        var sessionId = overview.Recent[0].Id;
        var pose = (await client.GetFromJsonAsync<PoseReplay>($"api/v1/sessions/{sessionId}/pose?maxFrames=100", ct))!;
        Assert.Equal(100, pose.FrameCount);
        Assert.All(pose.Frames[0].Points, v => Assert.NotNull(v));
    }
}
