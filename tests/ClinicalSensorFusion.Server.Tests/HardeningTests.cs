using System.Net;
using System.Net.Http.Json;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Server.Endpoints;

namespace ClinicalSensorFusion.Server.Tests;

/// <summary>Una chiave valida non basta: ogni dispositivo scrive solo le proprie rilevazioni, e solo blocchi decodificabili.</summary>
public sealed class IngestHardeningTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SessionUpload Session(string id, string audioId, string notes = "originale") => new(
        id, "PZ-OWN", "OP1", "Operatore", CaptureMode.BodyLidar, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
        notes, "iPhone13,3", null, [new AudioUpload(audioId, DateTimeOffset.UtcNow, 0.1, null)], []);

    private static ChunkUpload Chunk(int sequence = 0, int samples = 4)
    {
        var readings = Enumerable.Range(0, samples).Select(i => new SensorReading(SensorKind.Barometer, i, [0.1f, 101.2f])).ToArray();
        return new ChunkUpload(SensorKind.Barometer, sequence, 0, samples - 1, samples, SensorChunkCodec.Encode(readings));
    }

    private async Task<(string SessionId, string AudioId, HttpClient Owner)> CreateSessionAsync()
    {
        var (id, audioId) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var owner = server.CreateDeviceClient();
        (await owner.PutAsJsonAsync($"api/v1/ingest/sessions/{id}", Session(id, audioId), SyncJsonContext.Default.SessionUpload, Ct)).EnsureSuccessStatusCode();
        return (id, audioId, owner);
    }

    [Fact]
    public async Task Another_device_cannot_overwrite_a_session_its_chunks_or_its_audio()
    {
        var (id, audioId, owner) = await CreateSessionAsync();
        (await owner.PutAsync($"api/v1/ingest/sessions/{id}/audio/{audioId}", new ByteArrayContent(SyncEndToEndTests.Caf(100)), Ct)).EnsureSuccessStatusCode();

        var other = server.CreateDeviceClient(ServerFixture.OtherDeviceKey);
        var upsert = await other.PutAsJsonAsync($"api/v1/ingest/sessions/{id}", Session(id, audioId, "manomessa"), SyncJsonContext.Default.SessionUpload, Ct);
        Assert.Equal(HttpStatusCode.Conflict, upsert.StatusCode);
        var chunks = await other.PostAsJsonAsync($"api/v1/ingest/sessions/{id}/chunks", new ChunkBatch([Chunk()]), SyncJsonContext.Default.ChunkBatch, Ct);
        Assert.Equal(HttpStatusCode.NotFound, chunks.StatusCode);
        var audio = await other.PutAsync($"api/v1/ingest/sessions/{id}/audio/{audioId}", new ByteArrayContent(SyncEndToEndTests.Caf(5)), Ct);
        Assert.Equal(HttpStatusCode.NotFound, audio.StatusCode);

        var clinician = await server.CreateClinicianClientAsync();
        var detail = (await clinician.GetFromJsonAsync<SessionDetail>($"api/v1/sessions/{id}", Ct))!;
        Assert.Equal("originale", detail.Notes);
        Assert.Empty(detail.Streams);
        var wav = await clinician.GetByteArrayAsync($"api/v1/sessions/{id}/audio/{audioId}.wav", Ct);
        Assert.Equal(44 + 100 * 4, wav.Length);

        var denied = (await clinician.GetFromJsonAsync<PagedResult<AuditItem>>($"api/v1/audit?action=ingest.denied&entityId={id}", Ct))!;
        Assert.Equal(3, denied.Total);
        Assert.All(denied.Items, a => Assert.Equal("android-test", a.Actor));

        // Il proprietario continua a poter reinviare.
        (await owner.PutAsJsonAsync($"api/v1/ingest/sessions/{id}", Session(id, audioId, "aggiornata"), SyncJsonContext.Default.SessionUpload, Ct)).EnsureSuccessStatusCode();
        var accepted = await owner.PostAsJsonAsync($"api/v1/ingest/sessions/{id}/chunks", new ChunkBatch([Chunk()]), SyncJsonContext.Default.ChunkBatch, Ct);
        Assert.Equal(1, (await accepted.Content.ReadFromJsonAsync(SyncJsonContext.Default.ChunkBatchResult, Ct))!.Inserted);
    }

    [Fact]
    public async Task Sessions_sent_before_the_sensor_summary_existed_get_it_from_their_stored_chunks()
    {
        var (id, audioId) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var owner = server.CreateDeviceClient();
        var old = Session(id, audioId) with { Summary = new SessionSummary { DurationSeconds = 4, Mode = CaptureMode.BodyLidar } };
        (await owner.PutAsJsonAsync($"api/v1/ingest/sessions/{id}", old, SyncJsonContext.Default.SessionUpload, Ct)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"api/v1/ingest/sessions/{id}/chunks", new ChunkBatch([Chunk()]), SyncJsonContext.Default.ChunkBatch, Ct)).EnsureSuccessStatusCode();

        var clinician = await server.CreateClinicianClientAsync();
        var detail = (await clinician.GetFromJsonAsync<SessionDetail>($"api/v1/sessions/{id}", Ct))!;
        Assert.Contains(detail.Report, r => r is { Section: "Sensori ambientali", Label: "Barometro · Pressione", Value: "101,2 kPa" });
    }

    [Fact]
    public async Task Chunks_that_cannot_be_decoded_within_limits_are_rejected_before_storage()
    {
        var (id, _, owner) = await CreateSessionAsync();

        // Pochi byte compressi che si espandono oltre il limite del codec (16 MB di zeri).
        var bomb = SensorChunkCodec.Encode([.. Enumerable.Range(0, 64).Select(i => new SensorReading(SensorKind.Motion, i, new float[65_535]))]);
        Assert.True(bomb.Length < 100_000);
        var garbage = new byte[] { 1, 2, 3, 4, 5 };
        var valid = Chunk();
        ChunkUpload[] rejected =
        [
            valid with { Payload = bomb, SampleCount = 64 },
            valid with { Payload = garbage },
            valid with { SampleCount = valid.SampleCount + 1 },
            valid with { Kind = (SensorKind)999 },
        ];

        foreach (var chunk in rejected)
        {
            var response = await owner.PostAsJsonAsync($"api/v1/ingest/sessions/{id}/chunks", new ChunkBatch([chunk]), SyncJsonContext.Default.ChunkBatch, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var clinician = await server.CreateClinicianClientAsync();
        Assert.Empty((await clinician.GetFromJsonAsync<SessionDetail>($"api/v1/sessions/{id}", Ct))!.Streams);
    }

    [Fact]
    public async Task Audio_larger_than_the_configured_maximum_is_refused()
    {
        await using var small = new ServerFixture { Settings = { ["AudioStorage:MaxFileBytes"] = "1000" } };
        var device = small.CreateDeviceClient();
        var (id, audioId) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        (await device.PutAsJsonAsync($"api/v1/ingest/sessions/{id}", Session(id, audioId), SyncJsonContext.Default.SessionUpload, Ct)).EnsureSuccessStatusCode();

        var tooBig = await device.PutAsync($"api/v1/ingest/sessions/{id}/audio/{audioId}", new ByteArrayContent(SyncEndToEndTests.Caf(1000)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);

        // Senza Content-Length il limite scatta durante la scrittura, e non resta nessun file parziale.
        using var chunked = new StreamContent(new MemoryStream(SyncEndToEndTests.Caf(1000)));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/ingest/sessions/{id}/audio/{audioId}") { Content = chunked };
        request.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.BadRequest, (await device.SendAsync(request, Ct)).StatusCode);
        Assert.Empty(Directory.Exists(small.AudioRoot) ? Directory.GetFiles(small.AudioRoot, "*", SearchOption.AllDirectories) : []);
    }
}

public sealed class AuditTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Audit_log_requires_a_clinician()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateHttpsClient().GetAsync("api/v1/audit", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateDeviceClient().GetAsync("api/v1/audit", Ct)).StatusCode);
    }

    [Fact]
    public async Task Failed_logins_and_logouts_are_recorded_with_the_client_context()
    {
        var client = server.CreateHttpsClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csf-test/1.0");
        var intruder = "intruso-" + Guid.NewGuid().ToString("N")[..8];
        var failed = await client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(intruder, "sbagliata"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);

        (await client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(ServerFixture.UserName, ServerFixture.Password), Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("api/v1/auth/logout", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/v1/audit", Ct)).StatusCode);

        var clinician = await server.CreateClinicianClientAsync();
        var auth = (await clinician.GetFromJsonAsync<PagedResult<AuditItem>>("api/v1/audit?action=auth.", Ct))!;
        var failure = Assert.Single(auth.Items, a => a.Action == "auth.login.failed");
        Assert.Equal(intruder, failure.EntityId);
        Assert.Equal("anonimo", failure.Actor);
        Assert.Equal("csf-test/1.0", failure.UserAgent);

        var logout = Assert.Single(auth.Items, a => a.Action == "auth.logout");
        Assert.Equal(ServerFixture.UserName, logout.Actor);
        Assert.Equal("csf-test/1.0", logout.UserAgent);
        Assert.Equal(2, auth.Items.Count(a => a.Action == "auth.login"));

        // Più recenti per primi; anche la consultazione lascia traccia.
        Assert.Equal(auth.Items.OrderByDescending(a => a.At).Select(a => a.Id), auth.Items.Select(a => a.Id));
        var reads = (await clinician.GetFromJsonAsync<PagedResult<AuditItem>>($"api/v1/audit?action=audit.read&actor={ServerFixture.UserName}&pageSize=1", Ct))!;
        Assert.Equal(1, reads.Total);
        Assert.Single(reads.Items);
    }
}

public sealed class RateLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_route_is_rate_limited_per_client_address()
    {
        await using var server = new ServerFixture { Settings = { ["RateLimits:GlobalPerMinute"] = "3" } };
        var client = server.CreateHttpsClient();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/v1/patients", Ct)).StatusCode);
        }

        var limited = await client.GetAsync("api/v1/patients", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        // La sonda di liveness resta raggiungibile dal monitoraggio: non esegue controlli ed è esente dai limiti.
        // /health invece esegue i controlli ed è limitata, quindi con il budget di questo IP già esaurito è 429.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health/live", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("health", Ct)).StatusCode);
    }

    [Fact]
    public async Task Health_probes_are_limited_but_liveness_is_not()
    {
        await using var server = new ServerFixture { Settings = { ["RateLimits:HealthPerMinute"] = "2" } };
        var client = server.CreateHttpsClient();

        // /health e /health/ready condividono la partizione (la chiave è l'indirizzo IP): il margine è comune.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health/ready", Ct)).StatusCode);

        var limited = await client.GetAsync("health", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        // La liveness non esegue controlli e resta esente.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("health/live", Ct)).StatusCode);
    }

    [Fact]
    public async Task Ingest_is_rate_limited_per_device_key()
    {
        await using var server = new ServerFixture { Settings = { ["RateLimits:IngestPerMinute"] = "2" } };
        var path = $"api/v1/ingest/sessions/{Guid.NewGuid():N}/chunks";
        var body = new ChunkBatch([new ChunkUpload(Core.Sensors.SensorKind.Motion, 0, 0, 1, 1, [1])]);

        var device = server.CreateDeviceClient();
        Assert.Equal(HttpStatusCode.NotFound, (await device.PostAsJsonAsync(path, body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await device.PostAsJsonAsync(path, body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await device.PostAsJsonAsync(path, body, Ct)).StatusCode);

        // Un altro dispositivo dietro lo stesso indirizzo ha il proprio margine.
        var other = server.CreateDeviceClient(ServerFixture.OtherDeviceKey);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(path, body, Ct)).StatusCode);
    }
}
