using System.Net;
using System.Security.Cryptography;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Invio di una singola rilevazione (pulsante «Invia al server» a fine acquisizione).</summary>
public sealed class SyncTests : IDisposable
{
    private static readonly Uri Server = new("https://clinica.test/");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "csf-sync-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private (ClinicalRepository Repository, SessionSynchronizer Synchronizer, StubHandler Handler) Create(HttpStatusCode status)
    {
        var database = new ClinicalDatabase(
            Path.Combine(_dir, ClinicalDatabase.FileName),
            new FixedKeyProvider(RandomNumberGenerator.GetBytes(32)),
            NullLogger<ClinicalDatabase>.Instance);
        var repository = new ClinicalRepository(database);
        var handler = new StubHandler(status);
#pragma warning disable CA2000 // HttpClient e handler vivono quanto il test.
        var client = new ClinicalSyncClient(new HttpClient(handler));
#pragma warning restore CA2000
        return (repository, new SessionSynchronizer(repository, client, NullLogger<SessionSynchronizer>.Instance), handler);
    }

    private static async Task<ClinicalSession> AddSessionAsync(ClinicalRepository repository, bool completed)
    {
        var session = new ClinicalSession
        {
            Id = ClinicalSession.NewId(),
            SubjectCode = "PZ-001",
            OperatorId = "op1",
            OperatorName = "Operatore Uno",
            Mode = CaptureMode.BodyLidar,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        await repository.CreateSessionAsync(session);
        if (completed)
        {
            session.EndedAt = DateTimeOffset.UtcNow;
            session.Summary = new SessionSummary { DurationSeconds = 120, Mode = session.Mode };
            await repository.CompleteSessionAsync(session);
        }

        return session;
    }

    [Fact]
    public async Task Sending_one_session_marks_it_as_synced()
    {
        var (repository, synchronizer, handler) = Create(HttpStatusCode.OK);
        var session = await AddSessionAsync(repository, completed: true);

        var report = await synchronizer.SyncSessionAsync(Server, session.Id, _ => "/nessun/file");

        Assert.Empty(report.Errors);
        Assert.Equal(1, report.Sessions);
        Assert.Contains($"PUT /api/v1/ingest/sessions/{session.Id}", handler.Requests);
        Assert.NotNull((await repository.GetSessionAsync(session.Id))!.SyncedAt);
        Assert.Empty(await repository.ListUnsyncedSessionsAsync());
    }

    [Fact]
    public async Task A_server_error_is_reported_and_the_session_stays_pending()
    {
        var (repository, synchronizer, _) = Create(HttpStatusCode.Unauthorized);
        var session = await AddSessionAsync(repository, completed: true);

        var report = await synchronizer.SyncSessionAsync(Server, session.Id, _ => "/nessun/file");

        Assert.Single(report.Errors);
        Assert.Equal(0, report.Sessions);
        Assert.Null((await repository.GetSessionAsync(session.Id))!.SyncedAt);
    }

    [Fact]
    public async Task A_missing_audio_file_is_reported_and_the_session_stays_pending()
    {
        var (repository, synchronizer, handler) = Create(HttpStatusCode.OK);
        var session = await AddSessionAsync(repository, completed: true);
        var lost = new AudioRecording { Id = AudioRecording.NewId(), SessionId = session.Id, CreatedAt = DateTimeOffset.UtcNow };
        var kept = new AudioRecording { Id = AudioRecording.NewId(), SessionId = session.Id, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        await repository.AddAudioRecordingAsync(lost);
        await repository.AddAudioRecordingAsync(kept);
        Directory.CreateDirectory(_dir);
        var keptPath = Path.Combine(_dir, kept.Id + ".caf");
        await File.WriteAllBytesAsync(keptPath, "caff"u8.ToArray());

        var report = await synchronizer.SyncSessionAsync(Server, session.Id, a => a.Id == kept.Id ? keptPath : "/nessun/file");

        Assert.Contains("audio", Assert.Single(report.Errors), StringComparison.Ordinal);
        Assert.Equal(0, report.Sessions);
        Assert.Null((await repository.GetSessionAsync(session.Id))!.SyncedAt);
        // Il resto è comunque arrivato al server: l'audio ancora presente non aspetta quello perso.
        Assert.Contains($"PUT /api/v1/ingest/sessions/{session.Id}/audio/{kept.Id}", handler.Requests);
        Assert.DoesNotContain($"PUT /api/v1/ingest/sessions/{session.Id}/audio/{lost.Id}", handler.Requests);

        var pending = await synchronizer.SyncPendingAsync(Server, _ => "/nessun/file");
        Assert.StartsWith("PZ-001:", Assert.Single(pending.Errors), StringComparison.Ordinal);
        Assert.Equal(0, pending.Sessions);
    }

    [Fact]
    public async Task An_incomplete_session_is_not_sent()
    {
        var (repository, synchronizer, handler) = Create(HttpStatusCode.OK);
        var session = await AddSessionAsync(repository, completed: false);

        var report = await synchronizer.SyncSessionAsync(Server, session.Id, _ => "/nessun/file");

        Assert.Single(report.Errors);
        Assert.Empty(handler.Requests);
    }
}
