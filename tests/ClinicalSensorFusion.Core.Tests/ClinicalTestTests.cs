using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Punteggi dei test standardizzati e conteggio automatico delle alzate dalla sedia.</summary>
public class ClinicalTestTests
{
    private static SessionSummary Summary(double seconds, int? sitToStand = null, GaitSummary? gait = null) =>
        new() { DurationSeconds = seconds, Mode = CaptureMode.BodyLidar, SitToStandCount = sitToStand, Gait = gait };

    [Theory]
    [InlineData(11.9, false)]
    [InlineData(12.0, true)]
    public void Tug_flags_twelve_seconds_or_more(double seconds, bool flagged)
    {
        var outcome = ClinicalTests.Evaluate(ClinicalTests.TimedUpAndGo, Summary(seconds));
        Assert.Equal(seconds, outcome.Value);
        Assert.Equal(flagged, outcome.Flag);
    }

    [Fact]
    public void Ten_meter_walk_reports_speed_over_the_central_six_meters()
    {
        var outcome = ClinicalTests.Evaluate(ClinicalTests.TenMeterWalk, Summary(10));
        Assert.Equal(0.6, outcome.Value!.Value, 6);
        Assert.True(outcome.Flag); // sotto 0,7 m/s
        Assert.False(ClinicalTests.Evaluate(ClinicalTests.TenMeterWalk, Summary(5)).Flag); // 1,2 m/s
    }

    [Fact]
    public void Tandem_shorter_than_ten_seconds_is_flagged()
    {
        Assert.True(ClinicalTests.Evaluate(ClinicalTests.TandemStance, Summary(6.5)).Flag);
        var held = ClinicalTests.Evaluate(ClinicalTests.TandemStance, Summary(10.2));
        Assert.False(held.Flag);
        Assert.Equal(10, held.Value);
    }

    [Fact]
    public void Chair_stand_and_six_minute_walk_are_marked_as_estimates()
    {
        var chair = ClinicalTests.Evaluate(ClinicalTests.ChairStand30, Summary(30, sitToStand: 11));
        Assert.Equal(11, chair.Value);
        Assert.True(chair.Estimated);

        var walk = ClinicalTests.Evaluate(ClinicalTests.SixMinuteWalk, Summary(360, gait: new GaitSummary(600, 412.5, 100, 0, 0)));
        Assert.Equal(412.5, walk.Value);
        Assert.True(walk.Estimated);
        Assert.Null(ClinicalTests.Evaluate(ClinicalTests.SixMinuteWalk, Summary(360)).Value);
    }

    [Fact]
    public void Five_times_sit_to_stand_flags_above_twelve_seconds_and_reports_a_count_mismatch()
    {
        var slow = ClinicalTests.Evaluate(ClinicalTests.FiveTimesSitToStand, Summary(14, sitToStand: 4));
        Assert.True(slow.Flag);
        Assert.Contains("4", slow.Interpretation, StringComparison.Ordinal);
        Assert.False(ClinicalTests.Evaluate(ClinicalTests.FiveTimesSitToStand, Summary(9, sitToStand: 5)).Flag);
    }

    [Fact]
    public void Sit_to_stand_cycles_are_counted_with_hysteresis()
    {
        var aggregator = new SessionMetricsAggregator(CaptureMode.BodyLidar);
        var t = 0.0;
        void Knee(float degrees)
        {
            var values = new float[JointAngleCalculator.JointCount + 2];
            Array.Fill(values, float.NaN);
            values[(int)Joint.LeftKneeFlexion] = degrees;
            values[(int)Joint.RightKneeFlexion] = degrees;
            aggregator.Add(new SensorReading(SensorKind.JointAngles, t += 0.03, values));
        }

        // Tre alzate complete; le oscillazioni attorno a 50° (né seduto né in piedi) non contano.
        foreach (var degrees in (float[])[90, 85, 50, 20, 10, 55, 45, 55, 88, 40, 15, 92, 60, 30, 12, 48, 52])
        {
            Knee(degrees);
        }

        Assert.Equal(3, aggregator.Build(1, null, 0).SitToStandCount);
    }

    [Fact]
    public void Every_test_has_instructions_reference_and_source()
    {
        Assert.Equal(ClinicalTests.All.Count, ClinicalTests.All.Select(t => t.Id).Distinct().Count());
        Assert.All(ClinicalTests.All, t =>
        {
            Assert.NotEmpty(t.Steps);
            Assert.False(string.IsNullOrWhiteSpace(t.PatientScript));
            Assert.False(string.IsNullOrWhiteSpace(t.AudioGuide));
            Assert.False(string.IsNullOrWhiteSpace(t.Reference));
            Assert.False(string.IsNullOrWhiteSpace(t.Source));
            Assert.Same(t, ClinicalTests.Find(t.Id));
        });
    }
}
