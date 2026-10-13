using System.Text.Json;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fhir;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Tests;

public class FhirBundleTests
{
    private static ClinicalSession Session() => new()
    {
        Id = "sessione-segreta-123",
        SubjectCode = "MARIO-ROSSI-1950",
        OperatorId = "op1",
        OperatorName = "Dott.ssa Bianchi",
        Mode = CaptureMode.BodyLidar,
        StartedAt = new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero),
        Summary = new SessionSummary
        {
            DurationSeconds = 60,
            JointRanges = new() { [Joint.RightKneeFlexion] = new JointRange(2, 118, 40, 900) },
            Gait = new GaitSummary(42, double.NaN, null, 0, 0),
            SitToStandCount = 5,
            Audio = new AudioFeatures(60, -32.5, -3, 0.08, 0.4, 0),
            Test = new TestOutcome("5tsts", "Five Times Sit to Stand (5TSTS)", TestResultKind.Seconds, 13.4, false, true, "Oltre 12 secondi."),
            Sensors = new()
            {
                [SensorKind.Location] = new([new("Latitudine", "°", 45.46421, 45.46421, 45.46421, 45.46421, 45.46421, 1)]),
                [SensorKind.Barometer] = new([new("Pressione", "kPa", 101.25, 101.25, 101.25, 101.25, 101.25, 1)]),
                [SensorKind.HeartRate] = new([new("Frequenza cardiaca", "bpm", 61, 75, 68, 61, 75, 12)]),
            },
        },
    };

    private static List<JsonElement> Resources(string json) =>
        [.. JsonDocument.Parse(json).RootElement.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource"))];

    [Fact]
    public void Bundle_is_a_collection_with_one_report_that_references_every_observation()
    {
        var root = JsonDocument.Parse(FhirAnalysisBundle.ToJson(Session())).RootElement;
        Assert.Equal("Bundle", root.GetProperty("resourceType").GetString());
        Assert.Equal("collection", root.GetProperty("type").GetString());

        var entries = root.GetProperty("entry").EnumerateArray().ToList();
        var report = Assert.Single(entries, e => e.GetProperty("resource").GetProperty("resourceType").GetString() == "DiagnosticReport");
        var observations = entries.Where(e => e.GetProperty("resource").GetProperty("resourceType").GetString() == "Observation").ToList();
        Assert.Equal(entries.Count - 1, observations.Count);

        // In un Bundle i riferimenti puntano ai fullUrl: devono esistere tutti, senza doppioni.
        var urls = observations.Select(e => e.GetProperty("fullUrl").GetString()).ToList();
        Assert.Equal(urls.Count, urls.Distinct().Count());
        Assert.All(urls, url => Assert.StartsWith("urn:uuid:", url, StringComparison.Ordinal));
        Assert.Equal(urls, report.GetProperty("resource").GetProperty("result").EnumerateArray().Select(r => r.GetProperty("reference").GetString()));

        // Campi obbligatori di FHIR per le due risorse.
        Assert.All(entries, e =>
        {
            Assert.Equal("final", e.GetProperty("resource").GetProperty("status").GetString());
            Assert.True(e.GetProperty("resource").TryGetProperty("code", out _));
        });
    }

    [Fact]
    public void Indicators_keep_standard_codes_units_and_details()
    {
        var resources = Resources(FhirAnalysisBundle.ToJson(Session()));
        JsonElement Find(string id) => resources.Single(r => r.TryGetProperty("id", out var value) && value.GetString() == id);

        var heartRate = Find("heart-rate");
        Assert.Equal(FhirObservations.Loinc, heartRate.GetProperty("code").GetProperty("coding")[0].GetProperty("system").GetString());
        Assert.Equal("8867-4", heartRate.GetProperty("code").GetProperty("coding")[0].GetProperty("code").GetString());
        Assert.Equal("/min", heartRate.GetProperty("valueQuantity").GetProperty("code").GetString());

        var knee = Find("rom-right-knee-flexion");
        Assert.Equal(116, knee.GetProperty("valueQuantity").GetProperty("value").GetDouble());
        Assert.Equal(FhirObservations.IndicatorSystem, knee.GetProperty("code").GetProperty("coding")[0].GetProperty("system").GetString());
        Assert.Contains(knee.GetProperty("component").EnumerateArray(), c => c.GetProperty("valueQuantity").GetProperty("value").GetDouble() == 118);

        var test = Find("test");
        Assert.Equal(13.4, test.GetProperty("valueQuantity").GetProperty("value").GetDouble());
        Assert.Equal("A", test.GetProperty("interpretation")[0].GetProperty("coding")[0].GetProperty("code").GetString());

        Assert.Equal(5, Find("sit-to-stand").GetProperty("valueInteger").GetInt32());
        Assert.Equal(-32.5, Find("audio").GetProperty("valueQuantity").GetProperty("value").GetDouble());
        Assert.Equal(101.25, Find("sensor-barometer-0").GetProperty("valueQuantity").GetProperty("value").GetDouble());
        Assert.InRange(Find("quality").GetProperty("valueInteger").GetInt32(), 0, 100);
    }

    [Fact]
    public void Bundle_carries_nothing_that_identifies_patient_operator_place_or_time()
    {
        var json = FhirAnalysisBundle.ToJson(Session());
        foreach (var forbidden in (string[])["MARIO", "Bianchi", "op1", "sessione-segreta", "45.46", "atitud", "2026", "subject", "performer", "Patient"])
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unmeasured_values_do_not_break_the_json()
    {
        // La distanza del cammino è NaN (pedometro senza stima): JSON non ammette NaN.
        var json = FhirAnalysisBundle.ToJson(Session());
        Assert.DoesNotContain("NaN", json, StringComparison.Ordinal);
        var gait = Resources(json).Single(r => r.GetProperty("id").GetString() == "gait");
        Assert.DoesNotContain(gait.GetProperty("component").EnumerateArray(), c => c.GetProperty("code").GetProperty("coding")[0].GetProperty("code").GetString() == "gait-distance");
    }

    [Fact]
    public void R4_puts_measurement_limits_in_the_conclusion_and_R5_in_a_note()
    {
        JsonElement Report(FhirRelease release) =>
            Resources(FhirAnalysisBundle.ToJson(Session(), release)).Single(r => r.GetProperty("resourceType").GetString() == "DiagnosticReport");

        var r4 = Report(FhirRelease.R4);
        Assert.True(r4.TryGetProperty("conclusion", out _));
        Assert.False(r4.TryGetProperty("note", out _));

        var r5 = Report(FhirRelease.R5);
        Assert.True(r5.TryGetProperty("note", out var note));
        Assert.Equal(r4.GetProperty("conclusion").GetString(), note[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(FhirRelease.R4, "FHIR R4 (4.0.1)")]
    [InlineData(FhirRelease.R5, "FHIR R5 (5.0.0)")]
    public void Prompt_sends_the_session_as_a_fhir_bundle_of_the_chosen_release(FhirRelease release, string label)
    {
        var user = ClinicalPromptBuilder.Build(Session(), "Italiano", fhir: release)[1].Content;
        Assert.Contains(label, user, StringComparison.Ordinal);
        Assert.Contains("\"resourceType\": \"Bundle\"", user, StringComparison.Ordinal);
        Assert.Contains("\"resourceType\": \"DiagnosticReport\"", user, StringComparison.Ordinal);
        Assert.DoesNotContain("MARIO", user, StringComparison.Ordinal);
    }
}
