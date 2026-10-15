using ClinicalSensorFusion.Core.Sensors;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Avvio, arresto e rilascio delle sorgenti di una registrazione come gruppo.</summary>
public class SensorSourceGroupTests
{
    private sealed class FakeSource(string name, bool available = true, bool failOnStart = false, bool failOnStop = false) : ISensorSource
    {
        public List<string> Calls { get; } = [];

        public string DisplayName => name;

        public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Motion];

        public bool IsAvailable => available;

        public Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            return failOnStart ? Task.FromException(new InvalidOperationException(name)) : Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Calls.Add("stop");
            return failOnStop ? Task.FromException(new InvalidOperationException(name)) : Task.CompletedTask;
        }

        public void Dispose() => Calls.Add("dispose");
    }

    private sealed class NullSink : ISensorSink
    {
        public bool TryWrite(in SensorReading reading) => true;
    }

    [Fact]
    public async Task Starts_only_available_sources_and_stops_and_releases_all()
    {
        var motion = new FakeSource("movimento");
        var missing = new FakeSource("barometro", available: false);
        var group = new SensorSourceGroup([motion, missing], NullLogger.Instance);

        await group.StartAsync(new NullSink(), CancellationToken.None);
        await group.StopAsync();
        group.Dispose();

        Assert.Equal(["start", "stop", "dispose"], motion.Calls);
        Assert.Equal(["dispose"], missing.Calls);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public async Task A_failed_start_stops_what_already_started_and_releases_everything()
    {
        var first = new FakeSource("movimento");
        var broken = new FakeSource("microfono", failOnStart: true);
        var never = new FakeSource("gps");
        var group = new SensorSourceGroup([first, broken, never], NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => group.StartAsync(new NullSink(), CancellationToken.None));

        Assert.Equal("microfono", ex.Message);
        Assert.Equal(["start", "stop", "dispose"], first.Calls);
        Assert.Equal(["start", "dispose"], broken.Calls);
        Assert.Equal(["dispose"], never.Calls);
    }

    [Fact]
    public async Task One_failed_stop_does_not_leave_the_others_running()
    {
        var broken = new FakeSource("barometro", failOnStop: true);
        var after = new FakeSource("microfono");
        var group = new SensorSourceGroup([broken, after], NullLogger.Instance);
        await group.StartAsync(new NullSink(), CancellationToken.None);

        await group.StopAsync();

        Assert.Equal(["start", "stop"], after.Calls);
    }

    [Fact]
    public async Task Cannot_be_reused_after_release()
    {
        var group = new SensorSourceGroup([new FakeSource("movimento")], NullLogger.Instance);
        group.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => group.StartAsync(new NullSink(), CancellationToken.None));
    }
}
