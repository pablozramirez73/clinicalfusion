using System.Globalization;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using ClinicalSensorFusion.Core.Sync;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Affidabilità della misura e controllo del payload JSON mostrati a fine acquisizione.</summary>
public class ReliabilityTests
{
    // Le etichette attese sono in italiano: con la cultura invariante il testo sarebbe in inglese.
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private static SessionSummary GoodBodySummary() => new()
    {
        DurationSeconds = 60,
        Mode = CaptureMode.BodyLidar,
        MedianSubjectDistanceMeters = 2.5,
        SampleCounts = new() { [SensorKind.Motion] = 6000, [SensorKind.JointAngles] = 1500 },
        JointRanges = new() { [Joint.RightKneeFlexion] = new JointRange(2, 118, 40, 1500) },
    };

    [Fact]
    public void A_well_sampled_recording_scores_high()
    {
        var report = MeasurementReliability.Assess(GoodBodySummary(), Italian);
        Assert.Equal(100, report.Score);
        Assert.Equal(ReliabilityLevel.High, report.Level);
        Assert.DoesNotContain(report.Factors, f => f.Warning);
    }

    [Fact]
    public void Missing_vision_frames_and_lost_samples_lower_the_score_and_are_flagged()
    {
        var summary = GoodBodySummary() with
        {
            SampleCounts = new() { [SensorKind.Motion] = 6000, [SensorKind.JointAngles] = 300 }, // 5 fotogrammi/s
            DroppedSamples = 400,                                                                 // circa 6%
            MedianSubjectDistanceMeters = 6,
        };

        var report = MeasurementReliability.Assess(summary, Italian);

        Assert.Equal(100 - 30 - 30 - 10, report.Score);
        Assert.Equal(ReliabilityLevel.Low, report.Level);
        Assert.Contains(report.Factors, f => f.Label == "Riconoscimento del corpo" && f.Warning);
        Assert.Contains(report.Factors, f => f.Label == "Campioni persi" && f.Warning);
    }

    [Fact]
    public void Hands_mode_is_judged_on_hand_frames_not_body_frames()
    {
        var summary = new SessionSummary
        {
            DurationSeconds = 20,
            Mode = CaptureMode.HandsLidar,
            SampleCounts = new() { [SensorKind.Motion] = 2000, [SensorKind.HandLandmarks] = 500 },
        };

        var report = MeasurementReliability.Assess(summary, Italian);

        Assert.Equal(100, report.Score);
        Assert.Contains(report.Factors, f => f.Label == "Riconoscimento delle mani");
    }

    private static SessionUpload Upload(SessionSummary? summary, string subject = "PZ-001") => new(
        "0123456789abcdef0123456789abcdef", subject, "op1", "Operatore Uno", CaptureMode.BodyLidar,
        new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 12, 8, 1, 0, TimeSpan.Zero),
        "nota", "iPhone13,3", summary, [], []);

    [Fact]
    public void A_complete_payload_is_valid_and_its_fingerprint_is_stable()
    {
        var summary = GoodBodySummary() with { Tremor = new TremorResult(5.2, 0.6, 0.3, 12) };
        var first = PayloadReport.Inspect(Upload(summary), new ChunkStats(12, 9000, 250_000));
        var second = PayloadReport.Inspect(Upload(summary), new ChunkStats(12, 9000, 250_000));

        Assert.True(first.IsValid);
        Assert.True(first.RoundTrips);
        Assert.Empty(first.MissingFields);
        Assert.Equal(0, first.NonFiniteNumbers);
        Assert.Equal(64, first.Sha256.Length);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.NotEqual(first.Sha256, PayloadReport.Inspect(Upload(summary, "PZ-002"), first.Chunks).Sha256);
    }

    [Fact]
    public void Missing_fields_and_non_finite_numbers_are_reported()
    {
        var summary = GoodBodySummary() with { MedianSubjectDistanceMeters = double.NaN };
        var report = PayloadReport.Inspect(Upload(summary, subject: " "), new ChunkStats(0, 0, 0));

        Assert.False(report.IsValid);
        Assert.Contains("codice soggetto", report.MissingFields);
        Assert.Equal(1, report.NonFiniteNumbers);
        var rows = report.ToRows(Italian);
        Assert.Contains(rows, r => r.Label == "Valori non numerici" && r.Warning);
        Assert.Contains(rows, r => r.Label == "Dati dei sensori" && r.Warning);
    }
}
