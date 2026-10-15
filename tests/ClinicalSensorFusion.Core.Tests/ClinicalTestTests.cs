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

    [Theory]
    [InlineData(false, 1f, null)] // Android, o LiDAR spento: non applicabile, non "0%"
    [InlineData(true, 1f, 0.0)]   // LiDAR acceso ma mai valido: 0% è un dato vero
    [InlineData(true, 0f, 1.0)]
    public void Lidar_coverage_is_reported_only_when_the_lidar_was_used(bool lidarOn, float source, double? expected)
    {
        var aggregator = new SessionMetricsAggregator(CaptureMode.BodyLidar);
        for (var i = 0; i < 10; i++)
        {
            var values = new float[JointAngleCalculator.JointCount + 2];
            Array.Fill(values, float.NaN);
            values[(int)Joint.LeftKneeFlexion] = 30;
            values[^2] = source;
            aggregator.Add(new SensorReading(SensorKind.JointAngles, i * 0.03, values));
            if (lidarOn)
            {
                aggregator.Add(new SensorReading(SensorKind.LidarDepth, i * 0.03, [float.NaN, 0]));
            }
        }

        var summary = aggregator.Build(1, null, 0);
        Assert.Equal(expected, summary.LidarCoverage);
        Assert.DoesNotContain(MeasurementReliability.Assess(summary).Factors, f => expected is null && f.Label == "Copertura LiDAR");
        Assert.Equal(expected is not null, SummaryReport.Build(summary, System.Globalization.CultureInfo.InvariantCulture).Any(r => r.Label == "Copertura LiDAR"));
    }

    [Fact]
    public void Every_enabled_ambient_sensor_is_summarised_per_channel()
    {
        var aggregator = new SessionMetricsAggregator(CaptureMode.BodyLidar);
        aggregator.Add(new SensorReading(SensorKind.Barometer, 0, [0f, 101.2f]));
        aggregator.Add(new SensorReading(SensorKind.Barometer, 1, [0.5f, 101.0f]));
        aggregator.Add(new SensorReading(SensorKind.Location, 0, [45.46421f, 9.18998f, 120f, 30f, 0f]));
        aggregator.Add(new SensorReading(SensorKind.Location, 5, [45.46425f, 9.19002f, 121f, 8f, 0.4f]));
        // Android: solo i lux, la temperatura colore non esiste (NaN).
        aggregator.Add(new SensorReading(SensorKind.AmbientLight, 0, [300f, float.NaN]));
        aggregator.Add(new SensorReading(SensorKind.AmbientLight, 1, [500f, float.NaN]));
        aggregator.Add(new SensorReading(SensorKind.DeviceState, 0, [1f, 0.82f]));
        aggregator.Add(new SensorReading(SensorKind.Motion, 0, new float[16]));

        var summary = aggregator.Build(5, null, 0);
        var sensors = summary.Sensors!;

        Assert.Equal([SensorKind.Barometer, SensorKind.Location, SensorKind.AmbientLight, SensorKind.DeviceState], sensors.Keys);
        var pressure = Assert.Single(sensors[SensorKind.Barometer].Channels, c => c.Name == "Pressione");
        Assert.Equal("kPa", pressure.Unit);
        Assert.Equal(101.0, pressure.Min, 3);
        Assert.Equal(101.2, pressure.Max, 3);
        Assert.Equal(101.1, pressure.Mean, 3);
        Assert.Equal(101.2, pressure.First, 3);
        Assert.Equal(101.0, pressure.Last, 3);
        Assert.Equal(2, pressure.Samples);
        Assert.Equal("Intensità", Assert.Single(sensors[SensorKind.AmbientLight].Channels).Name);

        var report = SummaryReport.Build(summary, System.Globalization.CultureInfo.GetCultureInfo("it-IT"));
        Assert.Contains(report, r => r is { Kind: SummarySection.EnvironmentSensors, Label: "GPS · Posizione", Value: "45.46425, 9.19002" });
        Assert.Contains(report, r => r.Label == "GPS · Accuratezza" && r.Value == "±8 m (migliore ±8 m)" && !r.Warning);
        Assert.Contains(report, r => r.Label == "Luce ambientale · Intensità" && r.Value.StartsWith("300–500", StringComparison.Ordinal));
        Assert.Contains(report, r => r.Label == "Stato dispositivo · Batteria" && r.Value == "0,82 (scala 0–1)");

        // Le sezioni si riconoscono dall'enum in ogni lingua; il titolo è sempre quello tradotto della sezione.
        foreach (var culture in new[] { "it-IT", "en-US" }.Select(System.Globalization.CultureInfo.GetCultureInfo))
        {
            var rows = SummaryReport.Build(summary, culture);
            Assert.All(rows, r => Assert.Equal(SummaryReport.SectionTitle(r.Kind, culture), r.Section));
            Assert.Equal(report.Count(r => r.Kind == SummarySection.EnvironmentSensors), rows.Count(r => r.Kind == SummarySection.EnvironmentSensors));
            Assert.Contains(rows, r => r.Kind == SummarySection.DataQuality);
        }

        // Schede del dettaglio: una per sensore, ultimo valore in evidenza e intervallo solo se è cambiato.
        var cards = SummaryReport.Environment(summary, System.Globalization.CultureInfo.GetCultureInfo("it-IT"));
        Assert.Equal(["Barometro", "GPS", "Luce ambientale", "Stato dispositivo"], cards.Select(c => c.Sensor));
        var gps = cards[1].Tiles;
        Assert.Contains(gps, t => t is { Name: "Posizione", Value: "45.46425, 9.19002", Detail: "2 rilevamenti" });
        Assert.Contains(gps, t => t is { Name: "Accuratezza", Value: "±8 m", Warning: false });
        var light = Assert.Single(cards[2].Tiles);
        Assert.Equal(("Intensità", "300–500 · media 400"), (light.Name, light.Detail));
        Assert.StartsWith("500 ", light.Value, StringComparison.Ordinal);
        Assert.False(cards[3].Tiles.Single(t => t.Name == "Batteria").HasDetail);

        // Il riepilogo si rilegge identico dal JSON che va al server.
        var json = System.Text.Json.JsonSerializer.Serialize(summary, ClinicalSensorFusion.Core.Persistence.CsfJsonContext.Default.SessionSummary);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, ClinicalSensorFusion.Core.Persistence.CsfJsonContext.Default.SessionSummary)!;
        Assert.Equal(pressure, back.Sensors![SensorKind.Barometer].Channels.Single(c => c.Name == "Pressione"));

        // Verso il modello linguistico e nella condivisione le coordinate non escono.
        var shared = summary.WithoutLocation();
        Assert.DoesNotContain(SensorKind.Location, shared.Sensors!.Keys);
        Assert.Contains(SensorKind.Barometer, shared.Sensors.Keys);
        Assert.Contains(SensorKind.Location, summary.Sensors!.Keys);
    }

    [Fact]
    public void Five_rises_complete_the_test_when_seated_again_and_time_stops_at_sitting()
    {
        var aggregator = new SessionMetricsAggregator(CaptureMode.BodyLidar);
        var tracker = new TestCompletionTracker(ClinicalTests.FiveTimesSitToStand.Completion!);
        var t = 0.0;
        double? Knee(float degrees)
        {
            var values = new float[JointAngleCalculator.JointCount + 2];
            Array.Fill(values, float.NaN);
            values[(int)Joint.LeftKneeFlexion] = degrees;
            values[(int)Joint.RightKneeFlexion] = degrees;
            aggregator.Add(new SensorReading(SensorKind.JointAngles, t += 0.1, values));
            return tracker.Update(aggregator.Snapshot());
        }

        // Un'alzata fatta mentre la voce spiega, prima del «Via», non conta.
        aggregator.Add(new SensorReading(SensorKind.JointAngles, 0, Angles(90)));
        aggregator.Add(new SensorReading(SensorKind.JointAngles, 0, Angles(10)));
        Assert.Null(Knee(90));

        for (var rise = 1; rise <= 5; rise++)
        {
            Assert.Null(Knee(10));
            Assert.Equal(rise, tracker.Done);
            if (rise < 5)
            {
                Assert.Null(Knee(90));
                Assert.Null(Knee(90));
                Assert.Null(Knee(90));
                Assert.Null(Knee(90));
            }
        }

        // Quinta seduta: completato solo dopo 0,3 s da seduto, ma il tempo è quello in cui si è seduto.
        Assert.Null(Knee(90));
        var seatedAt = t;
        Assert.Null(Knee(92));
        Assert.Null(Knee(91));
        Assert.Equal(seatedAt, Knee(90)!.Value, 6);

        static float[] Angles(float knee)
        {
            var values = new float[JointAngleCalculator.JointCount + 2];
            Array.Fill(values, float.NaN);
            values[(int)Joint.LeftKneeFlexion] = knee;
            values[(int)Joint.RightKneeFlexion] = knee;
            return values;
        }
    }

    [Fact]
    public void Ten_taps_after_the_start_complete_finger_tapping()
    {
        var rule = ClinicalTests.FingerTapping.Completion!;
        var tracker = new TestCompletionTracker(rule);
        LiveMetrics Live(int taps, double elapsed) =>
            new(elapsed, null, null, null, null, 0, null, false, 0, new Dictionary<SensorKind, long>()) { FingerTaps = taps };

        // Tre tocchi di prova durante la spiegazione: il conteggio parte dal «Via».
        Assert.Null(tracker.Update(Live(3, 5)));
        Assert.Null(tracker.Update(Live(12, 9)));
        Assert.Equal(9, tracker.Done);
        Assert.Equal(9.4, tracker.Update(Live(13, 9.4)));
        Assert.Equal(rule.Count, tracker.Required);
    }

    [Fact]
    public void Only_tests_with_a_recognisable_task_close_by_themselves()
    {
        Assert.Equal(["tug", "5tsts", "ftap"], ClinicalTests.All.Where(t => t.Completion is not null).Select(t => t.Id));
        Assert.All(ClinicalTests.All, t => Assert.False(t.Completion is not null && t.AutoStopSeconds is not null));
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
