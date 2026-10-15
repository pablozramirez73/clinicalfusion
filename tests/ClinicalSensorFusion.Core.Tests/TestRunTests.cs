using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Esecuzione di un test dal «Via» alla chiusura: frasi previste, completamento e durata fissa.</summary>
public class TestRunTests
{
    private static LiveMetrics Live(double elapsed, int taps = 0) =>
        new(elapsed, null, null, null, null, 0, null, false, 0, new Dictionary<SensorKind, long>()) { FingerTaps = taps };

    [Fact]
    public void Nothing_happens_before_the_start()
    {
        var run = new TestRun(ClinicalTests.ChairStand30);

        Assert.Equal(default, run.Advance(null, Live(40)));
        Assert.Equal(TestRunState.WaitingForStart, run.State);
    }

    [Fact]
    public void Cues_are_spoken_once_at_their_time_and_a_fixed_test_stops_when_time_is_up()
    {
        var run = new TestRun(ClinicalTests.ChairStand30);

        Assert.Null(run.Advance(14.9, Live(20)).Cue);
        Assert.Equal("Continui così, è a metà.", run.Advance(15, Live(20)).Cue);
        Assert.Null(run.Advance(15.2, Live(20)).Cue);
        Assert.Equal("Ancora cinque secondi.", run.Advance(25, Live(30)).Cue);
        Assert.False(run.Advance(29.9, Live(35)).Finished);

        var end = run.Advance(30, Live(35));
        Assert.True(end.Finished);
        Assert.Null(end.CompletedAt);
        Assert.Equal(TestRunState.Finished, run.State);

        // Chiuso una volta sola, anche se il timer chiama ancora.
        Assert.False(run.Advance(30.25, Live(35)).Finished);
    }

    [Fact]
    public void A_completed_task_finishes_the_test_and_counts_only_after_the_start()
    {
        var run = new TestRun(ClinicalTests.FingerTapping);

        // Tre tocchi fatti mentre la voce spiegava: non contano.
        Assert.False(run.Advance(0, Live(5, taps: 3)).Finished);
        Assert.Equal(0, run.Done);
        Assert.Equal(10, run.Required);

        Assert.False(run.Advance(3, Live(8, taps: 12)).Finished);
        Assert.Equal(9, run.Done);

        var end = run.Advance(4, Live(9.4, taps: 14));
        Assert.True(end.Finished);
        Assert.Equal(10, run.Done);

        // Il risultato del finger tapping è una frequenza, non un tempo: nessun istante di fine da segnare.
        Assert.Null(end.CompletedAt);
    }

    [Fact]
    public void A_timed_test_reports_when_the_task_was_completed()
    {
        var timed = ClinicalTests.FingerTapping with { ResultKind = TestResultKind.Seconds };
        var run = new TestRun(timed);
        run.Advance(0, Live(5));

        Assert.Equal(9.4, run.Advance(4, Live(9.4, taps: 10)).CompletedAt);
    }

    [Fact]
    public void Tests_without_a_recognisable_task_show_no_progress()
    {
        var run = new TestRun(ClinicalTests.TenMeterWalk);

        Assert.False(run.Advance(120, Live(130)).Finished);
        Assert.Null(run.Done);
        Assert.Equal(TestRunState.Running, run.State);
    }
}
