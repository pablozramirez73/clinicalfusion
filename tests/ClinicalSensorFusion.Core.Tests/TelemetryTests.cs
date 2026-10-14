using System.Text.Json;
using ClinicalSensorFusion.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicalSensorFusion.Core.Tests;

public sealed class TelemetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "csf-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static List<JsonElement> ReadLines(IEnumerable<string> files) =>
        [.. files.SelectMany(File.ReadAllLines).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    [Fact]
    public async Task File_logger_writes_one_json_object_per_event_with_fields_scope_and_exception()
    {
        await using var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions { Directory = _dir });
        var logger = provider.CreateLogger("Test.Category");

        logger.LogDebug("sotto il livello minimo");
        using (logger.BeginScope(new Dictionary<string, object?> { ["SessionId"] = "abc" }))
        {
            logger.LogWarning(new EventId(42), new InvalidOperationException("rotto"), "Blocco {Kind}#{Seq} fallito\nseconda riga", "Motion", 7);
        }

        await provider.FlushAsync();
        var line = Assert.Single(ReadLines(provider.Files));
        Assert.Equal("Warning", line.GetProperty("level").GetString());
        Assert.Equal("Test.Category", line.GetProperty("category").GetString());
        Assert.Equal(42, line.GetProperty("eventId").GetInt32());
        Assert.Equal("Motion", line.GetProperty("fields").GetProperty("Kind").GetString());
        Assert.Equal(7, line.GetProperty("fields").GetProperty("Seq").GetInt32());
        Assert.Equal("abc", line.GetProperty("scope").GetProperty("SessionId").GetString());
        Assert.Contains("rotto", line.GetProperty("exception").GetString(), StringComparison.Ordinal);
        // Un a capo nel messaggio resta dentro la stringa JSON: non può fingere una seconda riga di log.
        Assert.Contains("seconda riga", line.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_logger_rotates_and_keeps_disk_usage_bounded()
    {
        var options = new RollingFileLoggerOptions { Directory = _dir, MaxFileBytes = 2_000, MaxFiles = 3 };
        await using (var provider = new RollingFileLoggerProvider(options))
        {
            var logger = provider.CreateLogger("Test");
            for (var i = 0; i < 400; i++)
            {
                logger.LogInformation("Riga numero {Index} con un po' di testo per riempire il file.", i);
                if (i % 50 == 0)
                {
                    await provider.FlushAsync();
                }
            }
        }

        var files = RollingFileLoggerProvider.ListFiles(options);
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 2_000));
        // Sopravvivono le righe più recenti.
        Assert.Equal(399, ReadLines(files).Max(l => l.GetProperty("fields").GetProperty("Index").GetInt32()));
    }

    [Fact]
    public void Operation_records_duration_with_outcome_and_collector_aggregates_by_tags()
    {
        using var collector = new MetricsCollector();
        // Strumenti propri del test: quelli veri sono condivisi con i test che girano in parallelo.
        var duration = CsfTelemetry.Meter.CreateHistogram<double>("csf.test.operation.duration", "s");
        var counter = CsfTelemetry.Meter.CreateCounter<long>("csf.test.dropped", "{sample}");
        using (var ok = CsfTelemetry.StartOperation(NullLogger.Instance, "test.op", duration))
        {
            ok.Succeed();
        }

        using (var failed = CsfTelemetry.StartOperation(NullLogger.Instance, "test.op", duration))
        {
            failed.Fail(new TimeoutException());
        }

        using (CsfTelemetry.StartOperation(NullLogger.Instance, "test.op", duration))
        {
            // Nessun esito dichiarato: conta come errore, mai come successo.
        }

        counter.Add(3, CsfTelemetry.Tag("kind", "TelemetryTest"));
        counter.Add(2, CsfTelemetry.Tag("kind", "TelemetryTest"));

        var points = collector.Snapshot();
        var durations = points.Where(p => p.Name == "csf.test.operation.duration").ToDictionary(p => p.Tags, p => p);
        Assert.Equal(1, durations["outcome=success"].Count);
        Assert.Equal(1, durations["outcome=timeout"].Count);
        Assert.Equal(1, durations["outcome=error"].Count);
        Assert.Equal("histogram", durations["outcome=success"].Kind);
        Assert.Equal("s", durations["outcome=success"].Unit);

        var dropped = Assert.Single(points, p => p.Name == "csf.test.dropped" && p.Tags == "kind=TelemetryTest");
        Assert.Equal("counter", dropped.Kind);
        Assert.Equal(5, dropped.Sum);
        Assert.Contains("csf.test.dropped{kind=TelemetryTest} n=2 total=5", MetricsCollector.Describe(points), StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_gives_stable_pseudonyms_and_single_line_text()
    {
        Assert.Equal(LogRedaction.Pseudonym("PZ-001"), LogRedaction.Pseudonym("PZ-001"));
        Assert.NotEqual(LogRedaction.Pseudonym("PZ-001"), LogRedaction.Pseudonym("PZ-002"));
        Assert.DoesNotContain("PZ-001", LogRedaction.Pseudonym("PZ-001"), StringComparison.Ordinal);
        Assert.Equal("-", LogRedaction.Pseudonym(null));
        Assert.Equal("a b", LogRedaction.Sanitize("a\nb"));
        Assert.Equal("abc…", LogRedaction.Sanitize("abcdef", maxLength: 3));
    }
}
