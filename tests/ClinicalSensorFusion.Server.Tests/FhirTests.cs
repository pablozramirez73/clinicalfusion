// Il modello FHIR di riferimento annota quasi ogni proprietà come annullabile: qui i null sono errori del test.
#nullable disable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Server.Endpoints;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Task = System.Threading.Tasks.Task;

namespace ClinicalSensorFusion.Server.Tests;

/// <summary>
/// Interfaccia HL7 FHIR R4. Ogni risposta viene riletta con il modello R4 di riferimento (Firely) in modalità
/// rigorosa: un elemento sconosciuto, un tipo sbagliato o un valore fuori dai codici ammessi fanno fallire il test.
/// </summary>
public sealed class FhirTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions FhirJson = new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static HttpClient s_clinician;
    private static string s_patientId;

    private static T Parse<T>(string json)
        where T : Resource => JsonSerializer.Deserialize<T>(json, FhirJson)!;

    private static async Task<T> GetAsync<T>(string path, HttpStatusCode expected = HttpStatusCode.OK)
        where T : Resource
    {
        var response = await s_clinician.GetAsync(path, Ct);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/fhir+json", response.Content.Headers.ContentType!.MediaType);
        return Parse<T>(await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Come <see cref="GetAsync{T}"/>, ma su un client di una fixture propria del test (tetti abbassati).</summary>
    private static async Task<T> ReadAsync<T>(HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
        where T : Resource
    {
        var response = await client.GetAsync(path, Ct);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/fhir+json", response.Content.Headers.ContentType!.MediaType);
        return Parse<T>(await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Riepilogo senza indicatori: una rilevazione che non produce osservazioni, comoda per i bundle grandi.</summary>
    private static SessionSummary WithoutIndicators => new() { DurationSeconds = 60, Mode = CaptureMode.BodyLidar };

    /// <summary>Riepilogo con un solo indicatore: esattamente una osservazione per rilevazione.</summary>
    private static SessionSummary HeartRateOnly => new()
    {
        DurationSeconds = 60,
        Mode = CaptureMode.BodyLidar,
        Sensors = new() { [SensorKind.HeartRate] = new([new ChannelStats("Frequenza cardiaca", "bpm", 62, 91, 74.4, 62, 80, 58)]) },
    };

    /// <summary>
    /// Semina nel database del server un paziente con N rilevazioni. Passare dall'ingestione HTTP significherebbe
    /// N richieste; il <c>DbContext</c> dello scope del server è la via diretta per provare i tetti.
    /// </summary>
    private static async Task<string> SeedPatientWithSessionsAsync(ServerFixture fixture, int sessions, SessionSummary summary)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.ClinicalDbContext>();
        var now = new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);
        var patient = new Data.Patient
        {
            Id = Guid.CreateVersion7(),
            Code = "PZ-BULK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            IsPlaceholder = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Patients.Add(patient);

        // Il riepilogo si serializza una volta sola: le rilevazioni seminate lo condividono.
        var json = JsonSerializer.Serialize(summary, CsfJsonContext.Default.SessionSummary);
        for (var i = 0; i < sessions; i++)
        {
            db.Sessions.Add(new Data.SessionEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                PatientId = patient.Id,
                SubjectCode = patient.Code,
                OperatorId = "OP1",
                OperatorName = "Dott.ssa Operatrice",
                Mode = CaptureMode.BodyLidar,
                StartedAt = now.AddMinutes(-i),
                EndedAt = now.AddMinutes(-i).AddMinutes(1),
                DeviceModel = "iPhone13,3",
                SummaryJson = json,
                ReceivedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(Ct);
        return patient.Id.ToString();
    }

    /// <summary>Un paziente con anagrafica completa e una rilevazione con tutti gli indicatori.</summary>
    public async ValueTask InitializeAsync()
    {
        if (s_clinician is not null)
        {
            return;
        }

        var summary = new SessionSummary
        {
            DurationSeconds = 60,
            Mode = CaptureMode.BodyLidar,
            JointRanges = new() { [Joint.RightKneeFlexion] = new JointRange(5, 115, 60, 900) },
            Tremor = new TremorResult(5.2, 0.7, 0.21, 12),
            Gait = new GaitSummary(42, 30.5, 104, 0, 0),
            SitToStandCount = 5,
            Sensors = new()
            {
                [SensorKind.HeartRate] = new([new ChannelStats("Frequenza cardiaca", "bpm", 62, 91, 74.4, 62, 80, 58)]),
                [SensorKind.PulseOximeter] = new([new ChannelStats("SpO2", "%", 96, 99, 97.5, 97, 98, 30)]),
                [SensorKind.BodyTemperature] = new([new ChannelStats("Temperatura", "°C", 36.6, 36.8, 36.7, 36.6, 36.8, 3)]),
            },
        };
        var upload = new SessionUpload(
            SessionId, "pz-fhir", "OP1", "Dott.ssa Operatrice", CaptureMode.BodyLidar,
            new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 12, 9, 31, 0, TimeSpan.Zero),
            "nota libera", "iPhone13,3", summary, [], []);
        (await server.CreateDeviceClient().PutAsJsonAsync($"api/v1/ingest/sessions/{SessionId}", upload, SyncJsonContext.Default.SessionUpload, Ct)).EnsureSuccessStatusCode();

        s_clinician = await server.CreateClinicianClientAsync();
        var patients = await s_clinician.GetFromJsonAsync<PagedResult<PatientListItem>>("api/v1/patients?search=PZ-FHIR", Ct);
        var patient = Assert.Single(patients!.Items);
        s_patientId = patient.Id.ToString();
        var detail = (await s_clinician.GetFromJsonAsync<PatientDetail>($"api/v1/patients/{patient.Id}", Ct))!;
        var update = await s_clinician.PutAsJsonAsync($"api/v1/patients/{patient.Id}", new PatientInput(
            "PZ-FHIR", "Maria", "Rossi", new DateOnly(1950, 3, 14), Data.Sex.Female, "RSSMRA50C54F205X", "+39 02 1234567",
            "maria@example.org", "Via Roma 1, Milano", "Parkinson", null, detail.Version), Ct);
        update.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Capability_statement_is_public_and_declares_what_the_server_does()
    {
        var response = await server.CreateHttpsClient().GetAsync("fhir/metadata", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var statement = Parse<CapabilityStatement>(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(FHIRVersion.N4_0_1, statement.FhirVersion);
        Assert.Equal(CapabilityStatementKind.Instance, statement.Kind);
        Assert.Contains("application/fhir+json", statement.Format);
        var rest = Assert.Single(statement.Rest);
        Assert.Equal(CapabilityStatement.RestfulCapabilityMode.Server, rest.Mode);
        Assert.Equal(["Patient", "Observation", "DiagnosticReport"], rest.Resource.Select(r => r.Type));
        Assert.All(rest.Resource, r => Assert.Equal(
            [CapabilityStatement.TypeRestfulInteraction.Read, CapabilityStatement.TypeRestfulInteraction.SearchType],
            r.Interaction.Select(i => i.Code!.Value)));
        Assert.Contains(rest.Resource[0].Operation, o => o.Name == "everything");
    }

    [Fact]
    public async Task Clinical_data_requires_authentication_and_device_keys_are_not_enough()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateHttpsClient().GetAsync("fhir/Patient", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateDeviceClient().GetAsync($"fhir/Patient/{s_patientId}/$everything", Ct)).StatusCode);
    }

    [Fact]
    public async Task Patient_is_read_with_identifiers_demographics_and_version()
    {
        var response = await s_clinician.GetAsync($"fhir/Patient/{s_patientId}", Ct);
        Assert.StartsWith("W/\"", response.Headers.ETag!.ToString(), StringComparison.Ordinal);
        var patient = Parse<Patient>(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(s_patientId, patient.Id);
        Assert.Contains(patient.Identifier, i => i is { System: "urn:clinical-sensor-fusion:patient-code", Value: "PZ-FHIR" });
        Assert.Contains(patient.Identifier, i => i is { System: "urn:oid:2.16.840.1.113883.2.9.4.3.2", Value: "RSSMRA50C54F205X" });
        Assert.Equal("Rossi", patient.Name[0].Family);
        Assert.Equal(["Maria"], patient.Name[0].Given);
        Assert.Equal("1950-03-14", patient.BirthDate);
        Assert.Equal(AdministrativeGender.Female, patient.Gender);
        Assert.Contains(patient.Telecom, t => t is { System: ContactPoint.ContactPointSystem.Email, Value: "maria@example.org" });
        Assert.NotNull(patient.Meta.LastUpdated);
    }

    [Theory]
    [InlineData("identifier=PZ-FHIR", 1)]
    [InlineData("identifier=urn:clinical-sensor-fusion:patient-code|pz-fhir", 1)]
    [InlineData("identifier=urn:oid:2.16.840.1.113883.2.9.4.3.2|RSSMRA50C54F205X", 1)]
    [InlineData("identifier=http://altro.example|PZ-FHIR", 0)]
    [InlineData("family=ross", 1)]
    [InlineData("name=mari&birthdate=1950-03-14", 1)]
    [InlineData("birthdate=1950-03-15", 0)]
    public async Task Patient_search_returns_a_searchset_bundle(string query, int expected)
    {
        var bundle = await GetAsync<Bundle>($"fhir/Patient?{query}");
        Assert.Equal(Bundle.BundleType.Searchset, bundle.Type);
        Assert.Equal(expected, bundle.Total);
        Assert.Equal(expected, bundle.Entry.Count);
        Assert.Contains(bundle.Link, l => l.Relation == "self");
        Assert.All(bundle.Entry, e =>
        {
            Assert.EndsWith($"/fhir/Patient/{s_patientId}", e.FullUrl, StringComparison.Ordinal);
            Assert.IsType<Patient>(e.Resource);
        });
    }

    [Fact]
    public async Task Vital_signs_use_loinc_ucum_and_the_fhir_vital_signs_profiles()
    {
        var bundle = await GetAsync<Bundle>($"fhir/Observation?patient=Patient/{s_patientId}&category=vital-signs");
        var byCode = bundle.Entry.Select(e => (Observation)e.Resource).ToDictionary(o => o.Code.Coding[0].Code);

        Assert.Equal(["2708-6", "8310-5", "8867-4"], byCode.Keys.Order());
        var heartRate = byCode["8867-4"];
        Assert.Equal("http://loinc.org", heartRate.Code.Coding[0].System);
        Assert.Equal(ObservationStatus.Final, heartRate.Status);
        Assert.Equal($"Patient/{s_patientId}", heartRate.Subject.Reference);
        var value = Assert.IsType<Quantity>(heartRate.Value);
        Assert.Equal((74.4m, "http://unitsofmeasure.org", "/min"), (value.Value, value.System, value.Code));
        Assert.Contains("http://hl7.org/fhir/StructureDefinition/heartrate", heartRate.Meta.Profile);
        Assert.Equal("2026-10-12T09:30:00.000Z", ((Period)heartRate.Effective).Start);
        Assert.Equal("iPhone13,3", heartRate.Device.Display);

        Assert.Contains(byCode["2708-6"].Code.Coding, c => c.Code == "59408-5");
        Assert.Equal("%", ((Quantity)byCode["2708-6"].Value).Code);
        Assert.Equal("Cel", ((Quantity)byCode["8310-5"].Value).Code);
    }

    [Fact]
    public async Task Observations_are_searchable_by_code_and_date_and_readable_by_id()
    {
        var steps = await GetAsync<Bundle>($"fhir/Observation?patient={s_patientId}&code=http://loinc.org|55423-8");
        var stepCount = Assert.IsType<Quantity>(((Observation)Assert.Single(steps.Entry).Resource).Value);
        Assert.Equal(42m, stepCount.Value);

        // Indicatore senza codice standard: sistema locale dichiarato, con minimo, massimo e media come componenti.
        var rom = await GetAsync<Observation>($"fhir/Observation/{SessionId}-rom-right-knee-flexion");
        Assert.Equal(("urn:clinical-sensor-fusion:indicator", "rom-right-knee-flexion"), (rom.Code.Coding[0].System, rom.Code.Coding[0].Code));
        Assert.Equal(110m, ((Quantity)rom.Value).Value);
        Assert.Equal(["angle-min", "angle-max", "angle-mean"], rom.Component.Select(c => c.Code.Coding[0].Code));
        Assert.Contains("±5–10°", rom.Note[0].Text, StringComparison.Ordinal);

        Assert.Equal(7, (await GetAsync<Bundle>($"fhir/Observation?patient={s_patientId}&date=ge2026-10-12&date=le2026-10-12")).Total);
        Assert.Equal(0, (await GetAsync<Bundle>($"fhir/Observation?patient={s_patientId}&date=gt2026-10-12")).Total);
        Assert.Equal(0, (await GetAsync<Bundle>($"fhir/Observation?patient={s_patientId}&code=http://snomed.info/sct|8867-4")).Total);
    }

    [Fact]
    public async Task Search_results_are_paged_with_a_next_link()
    {
        var first = await GetAsync<Bundle>($"fhir/Observation?patient={s_patientId}&_count=3");
        Assert.Equal(7, first.Total);
        Assert.Equal(3, first.Entry.Count);
        var next = Assert.Single(first.Link, l => l.Relation == "next").Url;

        var second = await GetAsync<Bundle>(new Uri(next).PathAndQuery.TrimStart('/'));
        Assert.Equal(3, second.Entry.Count);
        Assert.Empty(first.Entry.Select(e => e.FullUrl).Intersect(second.Entry.Select(e => e.FullUrl)));
    }

    [Fact]
    public async Task Date_gt_with_a_time_excludes_the_exact_instant()
    {
        // La rilevazione della fixture è alle 09:30:00Z. Con una data che porta anche l'ora, gt deve escludere
        // l'istante esatto: prima del fix si comportava come ge e lo includeva.
        Assert.Equal(7, (await GetAsync<Bundle>($"fhir/Observation?patient={s_patientId}&date=gt2026-10-12T09:29:59Z")).Total);
        Assert.Equal(0, (await GetAsync<Bundle>($"fhir/Observation?patient={s_patientId}&date=gt2026-10-12T09:30:00Z")).Total);
        Assert.Equal(7, (await GetAsync<Bundle>($"fhir/Observation?patient={s_patientId}&date=ge2026-10-12T09:30:00Z")).Total);
    }

    [Fact]
    public async Task Observation_total_is_exact_across_pages()
    {
        // 60 rilevazioni con una osservazione ciascuna: il totale deve essere 60 su entrambe le pagine.
        await using var fixture = new ServerFixture();
        var patientId = await SeedPatientWithSessionsAsync(fixture, sessions: 60, HeartRateOnly);
        var clinician = await fixture.CreateClinicianClientAsync();

        var first = await ReadAsync<Bundle>(clinician, $"fhir/Observation?patient={patientId}&_count=50");
        Assert.Equal(60, first.Total);
        Assert.Equal(50, first.Entry.Count);

        var next = Assert.Single(first.Link, l => l.Relation == "next").Url;
        var second = await ReadAsync<Bundle>(clinician, new Uri(next).PathAndQuery.TrimStart('/'));
        Assert.Equal(60, second.Total);
        Assert.Equal(10, second.Entry.Count);
        Assert.DoesNotContain(second.Link, l => l.Relation == "next");
    }

    [Fact]
    public async Task Observation_search_over_the_scan_cap_is_an_explicit_error()
    {
        // Code e category vivono nel riepilogo: oltre il tetto il server non può dare un totale veritiero.
        // Rifiuta, invece di restituire un campione spacciato per l'insieme completo.
        await using var fixture = new ServerFixture { Settings = { ["Fhir:MaxSessionsScanned"] = "2" } };
        var patientId = await SeedPatientWithSessionsAsync(fixture, sessions: 3, WithoutIndicators);
        var clinician = await fixture.CreateClinicianClientAsync();

        var outcome = await ReadAsync<OperationOutcome>(clinician, $"fhir/Observation?patient={patientId}", HttpStatusCode.RequestEntityTooLarge);
        var issue = Assert.Single(outcome.Issue);
        Assert.Equal(OperationOutcome.IssueType.TooCostly, issue.Code);
        Assert.Contains("3 rilevazioni", issue.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Everything_is_complete_beyond_the_old_scan_cap()
    {
        // Prima del fix l'esportazione si fermava a 2000 rilevazioni senza dirlo: 2001 prova che ora è completa.
        await using var fixture = new ServerFixture();
        var patientId = await SeedPatientWithSessionsAsync(fixture, sessions: 2_001, WithoutIndicators);
        var clinician = await fixture.CreateClinicianClientAsync();

        var bundle = await ReadAsync<Bundle>(clinician, $"fhir/Patient/{patientId}/$everything");
        Assert.Equal(2_002, bundle.Total);   // il paziente più un referto per rilevazione
        Assert.Equal(2_002, bundle.Entry.Count);
        Assert.DoesNotContain(bundle.Link, l => l.Relation == "next");
    }

    [Fact]
    public async Task Everything_over_the_export_cap_is_an_explicit_error_instead_of_a_truncation()
    {
        await using var fixture = new ServerFixture { Settings = { ["Fhir:MaxSessionsPerExport"] = "2" } };
        var patientId = await SeedPatientWithSessionsAsync(fixture, sessions: 3, WithoutIndicators);
        var clinician = await fixture.CreateClinicianClientAsync();

        var outcome = await ReadAsync<OperationOutcome>(clinician, $"fhir/Patient/{patientId}/$everything", HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(OperationOutcome.IssueType.TooCostly, Assert.Single(outcome.Issue).Code);
    }

    [Fact]
    public async Task Everything_exports_the_patient_with_reports_and_observations_whose_references_resolve()
    {
        var bundle = await GetAsync<Bundle>($"fhir/Patient/{s_patientId}/$everything");
        Assert.IsType<Patient>(bundle.Entry[0].Resource);
        var report = Assert.IsType<DiagnosticReport>(Assert.Single(bundle.Entry, e => e.Resource is DiagnosticReport).Resource);
        Assert.Equal(DiagnosticReport.DiagnosticReportStatus.Final, report.Status);
        Assert.Equal(SessionId, report.Id);

        var urls = bundle.Entry.Select(e => e.FullUrl).ToHashSet();
        Assert.Equal(7, report.Result.Count);
        Assert.All(report.Result, r => Assert.Contains(urls, u => u.EndsWith("/fhir/" + r.Reference, StringComparison.Ordinal)));
        Assert.Equal(9, bundle.Entry.Count);

        // Note libere e analisi IA non fanno parte dell'esportazione.
        var export = await s_clinician.GetStringAsync($"fhir/Patient/{s_patientId}/$everything", Ct);
        Assert.DoesNotContain("nota libera", export, StringComparison.Ordinal);

        var audit = (await s_clinician.GetFromJsonAsync<PagedResult<AuditItem>>($"api/v1/audit?action=fhir.export&entityId={s_patientId}", Ct))!;
        Assert.True(audit.Total >= 1);
    }

    [Theory]
    [InlineData("fhir/Patient/00000000-0000-0000-0000-000000000000", HttpStatusCode.NotFound, OperationOutcome.IssueType.NotFound)]
    [InlineData("fhir/Patient/non-un-id", HttpStatusCode.NotFound, OperationOutcome.IssueType.NotFound)]
    [InlineData("fhir/Observation/sconosciuta", HttpStatusCode.NotFound, OperationOutcome.IssueType.NotFound)]
    [InlineData("fhir/Medication", HttpStatusCode.NotFound, OperationOutcome.IssueType.NotSupported)]
    [InlineData("fhir/Patient?birthdate=14/03/1950", HttpStatusCode.BadRequest, OperationOutcome.IssueType.Invalid)]
    [InlineData("fhir/Observation?patient=abc", HttpStatusCode.BadRequest, OperationOutcome.IssueType.Invalid)]
    [InlineData("fhir/Patient?_format=xml", HttpStatusCode.NotAcceptable, OperationOutcome.IssueType.NotSupported)]
    public async Task Errors_are_operation_outcomes(string path, HttpStatusCode status, OperationOutcome.IssueType code)
    {
        var outcome = await GetAsync<OperationOutcome>(path, status);
        var issue = Assert.Single(outcome.Issue);
        Assert.Equal(OperationOutcome.IssueSeverity.Error, issue.Severity);
        Assert.Equal(code, issue.Code);
    }

    [Fact]
    public async Task The_interface_is_read_only()
    {
        var created = await s_clinician.PostAsync("fhir/Patient", new StringContent("{\"resourceType\":\"Patient\"}", System.Text.Encoding.UTF8, "application/fhir+json"), Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, created.StatusCode);
        var deleted = await s_clinician.DeleteAsync($"fhir/Patient/{s_patientId}", Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, deleted.StatusCode);
        Assert.Equal(OperationOutcome.IssueType.NotSupported, Parse<OperationOutcome>(await deleted.Content.ReadAsStringAsync(Ct)).Issue[0].Code);
        Assert.Equal(HttpStatusCode.OK, (await s_clinician.GetAsync($"fhir/Patient/{s_patientId}", Ct)).StatusCode);
    }
}
