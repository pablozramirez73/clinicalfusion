using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;

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
        var outcome = ClinicalTests.Evaluate(ClinicalTests.TimedUpAndGo, Summary(seconds), seconds);
        Assert.Equal(seconds, outcome.Value);
        Assert.Equal(flagged, outcome.Flag);
    }

    [Fact]
    public void Ten_meter_walk_reports_speed_over_the_central_six_meters()
    {
        var outcome = ClinicalTests.Evaluate(ClinicalTests.TenMeterWalk, Summary(10), 10);
        Assert.Equal(0.6, outcome.Value!.Value, 6);
        Assert.True(outcome.Flag); // sotto 0,7 m/s
        Assert.False(ClinicalTests.Evaluate(ClinicalTests.TenMeterWalk, Summary(5), 5).Flag); // 1,2 m/s
    }

    [Fact]
    public void Tandem_shorter_than_ten_seconds_is_flagged()
    {
        Assert.True(ClinicalTests.Evaluate(ClinicalTests.TandemStance, Summary(6.5), 6.5).Flag);
        var held = ClinicalTests.Evaluate(ClinicalTests.TandemStance, Summary(10.2), 10.2);
        Assert.False(held.Flag);
        Assert.Equal(10, held.Value);
    }

    [Fact]
    public void Chair_stand_and_six_minute_walk_are_marked_as_estimates()
    {
        var chair = ClinicalTests.Evaluate(ClinicalTests.ChairStand30, Summary(30, sitToStand: 11), 30);
        Assert.Equal(11, chair.Value);
        Assert.True(chair.Estimated);

        var walk = ClinicalTests.Evaluate(ClinicalTests.SixMinuteWalk, Summary(360, gait: new GaitSummary(600, 412.5, 100, 0, 0)), 360);
        Assert.Equal(412.5, walk.Value);
        Assert.True(walk.Estimated);
        Assert.Null(ClinicalTests.Evaluate(ClinicalTests.SixMinuteWalk, Summary(360), 360).Value);
    }

    [Fact]
    public void Five_times_sit_to_stand_flags_above_twelve_seconds_and_reports_a_count_mismatch()
    {
        var slow = ClinicalTests.Evaluate(ClinicalTests.FiveTimesSitToStand, Summary(14, sitToStand: 4), 14);
        Assert.True(slow.Flag);
        Assert.Contains("4", slow.Interpretation, StringComparison.Ordinal);
        Assert.False(ClinicalTests.Evaluate(ClinicalTests.FiveTimesSitToStand, Summary(9, sitToStand: 5), 9).Flag);
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

    [Fact]
    public void A_recording_closed_before_the_start_signal_has_no_score()
    {
        var outcome = ClinicalTests.Evaluate(ClinicalTests.TimedUpAndGo, Summary(20), testSeconds: null);
        Assert.Null(outcome.Value);
        Assert.False(outcome.Flag);
    }

    [Fact]
    public void Cues_fall_inside_the_duration_of_their_test_and_are_ordered()
    {
        Assert.All(ClinicalTests.All.Where(t => t.Cues.Count > 0), t =>
        {
            Assert.NotNull(t.AutoStopSeconds);
            Assert.Equal(t.Cues.OrderBy(c => c.AtSeconds), t.Cues);
            Assert.All(t.Cues, c => Assert.InRange(c.AtSeconds, 1, t.AutoStopSeconds!.Value - 1));
        });
    }

    /// <summary>Dieci aperture e chiusure regolari a 4 Hz, con ampiezza che cala nel tempo.</summary>
    private static FingerTapAnalyzer TenTaps(double startAt, double firstAmplitude = 1.0, double lastAmplitude = 0.5)
    {
        var analyzer = new FingerTapAnalyzer();
        for (var tap = 0; tap < 10; tap++)
        {
            var t = startAt + (tap * 0.25);
            var amplitude = firstAmplitude + ((lastAmplitude - firstAmplitude) * tap / 9.0);
            analyzer.AddAperture(t, amplitude);       // dita aperte
            analyzer.AddAperture(t + 0.06, 0.32);     // zona intermedia: non conta
            analyzer.AddAperture(t + 0.125, 0.1);     // tocco
        }

        return analyzer;
    }

    [Fact]
    public void Finger_taps_are_counted_with_frequency_decrement_and_rhythm()
    {
        var series = TenTaps(startAt: 0).Build()!;
        var metrics = FingerTapMetrics.From(series, fromTime: 0);

        Assert.Equal(10, metrics.Taps);
        Assert.Equal(4.0, metrics.FrequencyHz!.Value, 3);
        Assert.InRange(metrics.AmplitudeDecrement!.Value, 0.35, 0.45);
        Assert.InRange(metrics.IntervalVariability!.Value, 0, 0.01);
    }

    [Fact]
    public void Jitter_around_one_threshold_is_not_a_tap()
    {
        var analyzer = new FingerTapAnalyzer();
        foreach (var aperture in (double[])[0.9, 0.3, 0.35, 0.28, 0.33, 0.27, 0.45, 0.3])
        {
            analyzer.AddAperture(0, aperture);
        }

        Assert.Equal(0, analyzer.Count);
    }

    [Fact]
    public void Hand_landmarks_are_read_from_the_recorded_layout()
    {
        var analyzer = new FingerTapAnalyzer();
        float[] Frame(float indexTipY)
        {
            var values = new float[3 + (21 * 3)];
            values[0] = 1;                                   // una mano
            // polso (0) nell'origine, base dell'indice (5) a 0,2 in verticale: palmo = 0,2
            values[3 + (5 * 3) + 1] = 0.2f;
            // punta del pollice (4) a 0,2; punta dell'indice (8) a distanza variabile
            values[3 + (4 * 3) + 1] = 0.2f;
            values[3 + (8 * 3) + 1] = indexTipY;
            return values;
        }

        analyzer.Add(0.0, Frame(0.40f));   // apertura 1,0
        analyzer.Add(0.1, Frame(0.21f));   // apertura 0,05: tocco
        analyzer.Add(0.2, Frame(0.40f));
        analyzer.Add(0.3, Frame(0.21f));

        Assert.Equal(2, analyzer.Count);
    }

    [Fact]
    public void Finger_tapping_scores_only_the_taps_after_the_start_signal()
    {
        // La registrazione dura 12,5 s: il «Via» arriva a 10 s e i dieci tocchi seguono.
        var analyzer = TenTaps(startAt: 10);
        analyzer.AddAperture(3.0, 1.0);
        var early = new FingerTapAnalyzer();
        early.AddAperture(3.0, 1.0);
        early.AddAperture(3.1, 0.1); // un tocco durante la spiegazione
        var all = analyzer.Build()!;
        var series = new FingerTapSeries([.. early.Build()!.Times, .. all.Times], [.. early.Build()!.Amplitudes, .. all.Amplitudes]);
        var summary = new SessionSummary { DurationSeconds = 12.5, Mode = CaptureMode.HandsLidar, FingerTaps = series };

        var outcome = ClinicalTests.Evaluate(ClinicalTests.FingerTapping, summary, testSeconds: 2.5);

        Assert.Equal(4.0, outcome.Value!.Value, 3);
        Assert.Contains("10", outcome.Interpretation, StringComparison.Ordinal);
        Assert.True(outcome.Estimated);
        Assert.Equal(CaptureMode.HandsLidar, ClinicalTests.FingerTapping.Mode);
        Assert.Contains(ClinicalTests.FingerTapping, ClinicalTests.For(CaptureMode.HandsLidar));
        Assert.DoesNotContain(ClinicalTests.FingerTapping, ClinicalTests.For(CaptureMode.BodyLidar));
    }
}
