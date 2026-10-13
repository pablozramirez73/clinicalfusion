using ClinicalSensorFusion.Core.Localization;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Fhir;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using ClinicalSensorFusion.Server.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Endpoints;

/// <summary>
/// Interfaccia HL7 FHIR R4 in sola lettura su <c>/fhir</c>: <c>metadata</c> (CapabilityStatement), lettura e
/// ricerca di Patient, Observation e DiagnosticReport, esportazione completa di un paziente con
/// <c>Patient/{id}/$everything</c>. Risposte in <c>application/fhir+json</c>, errori come OperationOutcome.
/// Stessa autenticazione dei clinici (cookie della dashboard o token JWT); ogni accesso è nel registro di audit.
/// </summary>
public static class FhirEndpoints
{
    public const string ContentType = "application/fhir+json; charset=utf-8";

    private const int DefaultCount = 50;
    private const int MaxCount = 200;

    /// <summary>Tetto alle rilevazioni lette per una ricerca: le osservazioni si generano dal riepilogo di ciascuna.</summary>
    private const int MaxSessionsScanned = 2_000;

    public static WebApplication MapFhir(this WebApplication app)
    {
        var fhir = app.MapGroup("/fhir").WithTags("HL7 FHIR R4").AddEndpointFilter(RejectUnsupportedFormat);
        fhir.MapGet("/metadata", Metadata).AllowAnonymous();

        var secured = fhir.MapGroup("").RequireAuthorization(Policies.Clinician);
        secured.MapGet("/Patient", SearchPatientsAsync);
        secured.MapGet("/Patient/{id}", ReadPatientAsync);
        secured.MapGet("/Patient/{id}/$everything", EverythingAsync);
        secured.MapGet("/Observation", SearchObservationsAsync);
        secured.MapGet("/Observation/{id}", ReadObservationAsync);
        secured.MapGet("/DiagnosticReport", SearchReportsAsync);
        secured.MapGet("/DiagnosticReport/{id}", ReadReportAsync);

        // Tipo di risorsa non supportato o percorso sconosciuto: 404 con OperationOutcome, come chiede la specifica.
        // Le scritture (POST, PUT, PATCH, DELETE) ricevono 405: l'interfaccia è in sola lettura.
        fhir.Map("/{**path}", (HttpContext http) => HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
            ? Outcome(StatusCodes.Status404NotFound, "not-supported", "Risorsa o interazione FHIR non supportata: vedi /fhir/metadata.")
            : Outcome(StatusCodes.Status405MethodNotAllowed, "not-supported", Loc.T("Interfaccia FHIR in sola lettura: scritture non ammesse.", Ui.Culture)));
        return app;
    }

    private static string Base(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}/fhir";

    private static IResult Fhir(JsonObject resource, int status = StatusCodes.Status200OK) =>
        Results.Text(resource.ToJsonString(), ContentType, statusCode: status);

    private static IResult Outcome(int status, string code, string diagnostics) =>
        Fhir(FhirMapper.OperationOutcome("error", code, diagnostics), status);

    private static IResult NotFound(string type, string id) =>
        Outcome(StatusCodes.Status404NotFound, "not-found", $"{type}/{id} non esiste.");

    /// <summary>Solo JSON: una richiesta esplicita di XML o Turtle riceve 406 invece di un formato diverso da quello atteso.</summary>
    private static async ValueTask<object?> RejectUnsupportedFormat(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var format = context.HttpContext.Request.Query["_format"].ToString();
        return format.Length > 0 && !format.Contains("json", StringComparison.OrdinalIgnoreCase)
            ? Outcome(StatusCodes.Status406NotAcceptable, "not-supported", Loc.T("È supportato solo il formato JSON (application/fhir+json).", Ui.Culture))
            : await next(context);
    }

    private static IResult Metadata(HttpContext http)
    {
        static JsonObject Param(string name, string type, string documentation) => new() { ["name"] = name, ["type"] = type, ["documentation"] = documentation };
        static JsonArray ReadAndSearch() => new(new JsonObject { ["code"] = "read" }, new JsonObject { ["code"] = "search-type" });

        var statement = new JsonObject
        {
            ["resourceType"] = "CapabilityStatement",
            ["status"] = "active",
            ["date"] = "2026-10-13",
            ["publisher"] = "Clinical Sensor Fusion",
            ["kind"] = "instance",
            ["software"] = new JsonObject { ["name"] = "Clinical Sensor Fusion Server", ["version"] = ServerTelemetry.Version },
            ["implementation"] = new JsonObject { ["description"] = Loc.T("Rilevazioni di movimento, postura e parametri vitali da smartphone", Ui.Culture), ["url"] = Base(http) },
            ["fhirVersion"] = FhirMapper.FhirVersion,
            ["format"] = new JsonArray("json", "application/fhir+json"),
            ["rest"] = new JsonArray(new JsonObject
            {
                ["mode"] = "server",
                ["documentation"] = Loc.T("Sola lettura. Le risorse sono generate dai dati archiviati; non si accettano scritture.", Ui.Culture),
                ["security"] = new JsonObject
                {
                    ["cors"] = false,
                    ["description"] = Loc.T("Intestazione Authorization: Bearer con il token JWT di POST /api/v1/auth/token (credenziali dei clinici), oppure il cookie di sessione della dashboard.", Ui.Culture),
                },
                ["resource"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "Patient",
                        ["interaction"] = ReadAndSearch(),
                        ["searchParam"] = new JsonArray(
                            Param("_id", "token", Loc.T("Identificativo della risorsa", Ui.Culture)),
                            Param("identifier", "token", Loc.T("Codice paziente o codice fiscale, anche come sistema|valore", Ui.Culture)),
                            Param("name", "string", Loc.T("Parte del nome o del cognome", Ui.Culture)),
                            Param("family", "string", Loc.T("Parte del cognome", Ui.Culture)),
                            Param("birthdate", "date", "Data di nascita (uguaglianza)")),
                        ["operation"] = new JsonArray(new JsonObject { ["name"] = "everything", ["definition"] = "http://hl7.org/fhir/OperationDefinition/Patient-everything" }),
                    },
                    new JsonObject
                    {
                        ["type"] = "Observation",
                        ["supportedProfile"] = new JsonArray(
                            "http://hl7.org/fhir/StructureDefinition/heartrate",
                            "http://hl7.org/fhir/StructureDefinition/oxygensat",
                            "http://hl7.org/fhir/StructureDefinition/bodytemp"),
                        ["interaction"] = ReadAndSearch(),
                        ["searchParam"] = new JsonArray(
                            Param("patient", "reference", "Paziente (identificativo o Patient/identificativo)"),
                            Param("code", "token", Loc.T("Codice LOINC o locale, anche come sistema|codice", Ui.Culture)),
                            Param("category", "token", "vital-signs, activity, exam"),
                            Param("date", "date", Loc.T("Inizio della rilevazione, con prefissi ge, gt, le, lt, eq", Ui.Culture))),
                    },
                    new JsonObject
                    {
                        ["type"] = "DiagnosticReport",
                        ["interaction"] = ReadAndSearch(),
                        ["searchParam"] = new JsonArray(
                            Param("patient", "reference", "Paziente (identificativo o Patient/identificativo)"),
                            Param("date", "date", Loc.T("Inizio della rilevazione, con prefissi ge, gt, le, lt, eq", Ui.Culture))),
                    }),
            }),
        };
        return Fhir(statement);
    }

    private static async Task<IResult> ReadPatientAsync(string id, ClinicalDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var patient = Guid.TryParse(id, out var guid) ? await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == guid, ct) : null;
        if (patient is null)
        {
            return NotFound("Patient", id);
        }

        audit.Record(http.User, "fhir.patient.read", id);
        await db.SaveChangesAsync(ct);
        http.Response.Headers.ETag = $"W/\"{patient.Version}\"";
        http.Response.Headers.LastModified = patient.UpdatedAt.ToString("R", CultureInfo.InvariantCulture);
        return Fhir(FhirMapper.Patient(patient));
    }

    private static async Task<IResult> SearchPatientsAsync(ClinicalDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var q = http.Request.Query;
        var query = db.Patients.AsNoTracking();
        if (q["_id"].ToString() is { Length: > 0 } id)
        {
            query = Guid.TryParse(id, out var guid) ? query.Where(p => p.Id == guid) : query.Where(_ => false);
        }

        if (q["identifier"].ToString() is { Length: > 0 } identifier)
        {
            var (system, value) = Token(identifier);
            var code = Patient.NormalizeCode(value);
            query = system switch
            {
                FhirMapper.PatientCodeSystem => query.Where(p => p.Code == code),
                FhirMapper.FiscalCodeSystem => query.Where(p => p.FiscalCode == code),
                null => query.Where(p => p.Code == code || p.FiscalCode == code),
                _ => query.Where(_ => false),
            };
        }

        if (q["name"].ToString() is { Length: > 0 } name)
        {
            var pattern = Like(name);
            query = query.Where(p => EF.Functions.ILike(p.LastName ?? "", pattern) || EF.Functions.ILike(p.FirstName ?? "", pattern));
        }

        if (q["family"].ToString() is { Length: > 0 } family)
        {
            var pattern = Like(family);
            query = query.Where(p => EF.Functions.ILike(p.LastName ?? "", pattern));
        }

        if (q["birthdate"].ToString() is { Length: > 0 } birthdate)
        {
            if (!DateOnly.TryParseExact(birthdate.StartsWith("eq", StringComparison.Ordinal) ? birthdate[2..] : birthdate, "yyyy-MM-dd", out var date))
            {
                return Outcome(StatusCodes.Status400BadRequest, "invalid", Loc.T("birthdate deve essere nel formato AAAA-MM-GG.", Ui.Culture));
            }

            query = query.Where(p => p.BirthDate == date);
        }

        var (count, offset) = Paging(q);
        var total = await query.CountAsync(ct);
        var page = await query.OrderBy(p => p.Code).Skip(offset).Take(count).ToListAsync(ct);

        audit.Record(http.User, "fhir.patient.search");
        await db.SaveChangesAsync(ct);
        return Fhir(Bundle(http, total, offset, count, page.Select(p => ($"Patient/{p.Id}", FhirMapper.Patient(p)))));
    }

    private static async Task<IResult> EverythingAsync(string id, ClinicalDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var patient = Guid.TryParse(id, out var guid) ? await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == guid, ct) : null;
        if (patient is null)
        {
            return NotFound("Patient", id);
        }

        var entries = new List<(string, JsonObject)> { ($"Patient/{patient.Id}", FhirMapper.Patient(patient)) };
        foreach (var session in await db.Sessions.AsNoTracking().Where(s => s.PatientId == guid).OrderBy(s => s.StartedAt).Take(MaxSessionsScanned).ToListAsync(ct))
        {
            var observations = FhirMapper.Observations(session, Summary(session));
            entries.Add(($"DiagnosticReport/{session.Id}", FhirMapper.DiagnosticReport(session, observations.Select(o => $"{session.Id}-{o.Key}"))));
            entries.AddRange(observations.Select(o => ($"Observation/{session.Id}-{o.Key}", o.Resource)));
        }

        audit.Record(http.User, "fhir.export", id);
        await db.SaveChangesAsync(ct);

        var baseUrl = Base(http);
        http.Response.Headers.ContentDisposition = $"inline; filename=\"paziente-{patient.Code}.fhir.json\"";
        return Fhir(FhirMapper.SearchBundle(
            $"{baseUrl}/Patient/{patient.Id}/$everything", null, entries.Count,
            entries.Select(e => ($"{baseUrl}/{e.Item1}", e.Item2))));
    }

    private static async Task<IResult> ReadObservationAsync(string id, ClinicalDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
    {
        // Identificativo = {id rilevazione di 32 caratteri}-{indicatore}.
        var session = id.Length > 33 && id[32] == '-' ? await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id.Substring(0, 32), ct) : null;
        var observation = session is null ? null : FhirMapper.Observations(session, Summary(session)).FirstOrDefault(o => o.Key == id[33..]).Resource;
        if (observation is null)
        {
            return NotFound("Observation", id);
        }

        audit.Record(http.User, "fhir.observation.read", session!.Id);
        await db.SaveChangesAsync(ct);
        return Fhir(observation);
    }

    private static async Task<IResult> SearchObservationsAsync(ClinicalDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var q = http.Request.Query;
        if (!TrySessions(db, q, out var sessions, out var problem))
        {
            return problem!;
        }

        var (codeSystem, code) = q["code"].ToString() is { Length: > 0 } c ? Token(c) : (null, null);
        var (_, category) = q["category"].ToString() is { Length: > 0 } cat ? Token(cat) : (null, null);

        var all = new List<(string, JsonObject)>();
        foreach (var session in await sessions.OrderByDescending(s => s.StartedAt).Take(MaxSessionsScanned).ToListAsync(ct))
        {
            foreach (var (key, resource) in FhirMapper.Observations(session, Summary(session)))
            {
                if (code is not null && !HasCoding(resource["code"], codeSystem, code))
                {
                    continue;
                }

                if (category is not null && !resource["category"]!.AsArray().Any(k => HasCoding(k, null, category)))
                {
                    continue;
                }

                all.Add(($"Observation/{session.Id}-{key}", resource));
            }
        }

        var (count, offset) = Paging(q);
        audit.Record(http.User, "fhir.observation.search", PatientId(q)?.ToString());
        await db.SaveChangesAsync(ct);
        return Fhir(Bundle(http, all.Count, offset, count, all.Skip(offset).Take(count)));
    }

    private static async Task<IResult> ReadReportAsync(string id, ClinicalDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return NotFound("DiagnosticReport", id);
        }

        audit.Record(http.User, "fhir.report.read", id);
        await db.SaveChangesAsync(ct);
        return Fhir(Report(session));
    }

    private static async Task<IResult> SearchReportsAsync(ClinicalDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var q = http.Request.Query;
        if (!TrySessions(db, q, out var sessions, out var problem))
        {
            return problem!;
        }

        var (count, offset) = Paging(q);
        var total = await sessions.CountAsync(ct);
        var page = await sessions.OrderByDescending(s => s.StartedAt).Skip(offset).Take(count).ToListAsync(ct);
        audit.Record(http.User, "fhir.report.search", PatientId(q)?.ToString());
        await db.SaveChangesAsync(ct);
        return Fhir(Bundle(http, total, offset, count, page.Select(s => ($"DiagnosticReport/{s.Id}", Report(s)))));
    }

    private static JsonObject Report(SessionEntity session) =>
        FhirMapper.DiagnosticReport(session, FhirMapper.Observations(session, Summary(session)).Select(o => $"{session.Id}-{o.Key}"));

    private static SessionSummary? Summary(SessionEntity session) =>
        session.SummaryJson is null ? null : JsonSerializer.Deserialize(session.SummaryJson, CsfJsonContext.Default.SessionSummary);

    /// <summary>Rilevazioni filtrate per <c>patient</c> (o <c>subject</c>) e <c>date</c>.</summary>
    private static bool TrySessions(ClinicalDbContext db, IQueryCollection q, out IQueryable<SessionEntity> sessions, out IResult? problem)
    {
        sessions = db.Sessions.AsNoTracking();
        problem = null;

        var reference = q["patient"].ToString() is { Length: > 0 } p ? p : q["subject"].ToString();
        if (reference.Length > 0)
        {
            if (PatientId(q) is not { } patientId)
            {
                problem = Outcome(StatusCodes.Status400BadRequest, "invalid", Loc.T("patient deve essere un identificativo o Patient/identificativo.", Ui.Culture));
                return false;
            }

            sessions = sessions.Where(s => s.PatientId == patientId);
        }

        foreach (var value in q["date"])
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var prefix = value.Length > 2 && char.IsLetter(value[0]) ? value[..2] : "eq";
            var text = prefix == "eq" && !char.IsLetter(value[0]) ? value : value[2..];
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                problem = Outcome(StatusCodes.Status400BadRequest, "invalid", string.Format(System.Globalization.CultureInfo.InvariantCulture, Loc.T("date non valida: {0}.", Ui.Culture), value));
                return false;
            }

            // Una data senza ora indica l'intero giorno.
            var dayOnly = text.Length <= 10;
            var end = dayOnly ? date.AddDays(1) : date;
            switch (prefix)
            {
                case "ge": sessions = sessions.Where(s => s.StartedAt >= date); break;
                case "gt": sessions = sessions.Where(s => s.StartedAt >= end); break;
                case "le": sessions = sessions.Where(s => s.StartedAt < (dayOnly ? end : date.AddTicks(1))); break;
                case "lt": sessions = sessions.Where(s => s.StartedAt < date); break;
                case "eq": sessions = dayOnly ? sessions.Where(s => s.StartedAt >= date && s.StartedAt < end) : sessions.Where(s => s.StartedAt == date); break;
                default:
                    problem = Outcome(StatusCodes.Status400BadRequest, "invalid", string.Format(System.Globalization.CultureInfo.InvariantCulture, Loc.T("Prefisso di data non supportato: {0}.", Ui.Culture), prefix));
                    return false;
            }
        }

        return true;
    }

    private static Guid? PatientId(IQueryCollection q)
    {
        var reference = q["patient"].ToString() is { Length: > 0 } p ? p : q["subject"].ToString();
        var id = reference.StartsWith("Patient/", StringComparison.Ordinal) ? reference["Patient/".Length..] : reference;
        return Guid.TryParse(id, out var guid) ? guid : null;
    }

    /// <summary>Parametro di tipo token: <c>codice</c> oppure <c>sistema|codice</c>.</summary>
    private static (string? System, string Value) Token(string token)
    {
        var bar = token.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? (null, token) : (token[..bar] is { Length: > 0 } system ? system : null, token[(bar + 1)..]);
    }

    private static bool HasCoding(JsonNode? codeableConcept, string? system, string code) =>
        codeableConcept?["coding"] is JsonArray codings && codings.Any(c =>
            (string?)c?["code"] == code && (system is null || (string?)c?["system"] == system));

    private static string Like(string value) => "%" + value.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static (int Count, int Offset) Paging(IQueryCollection q) => (
        int.TryParse(q["_count"], out var count) ? Math.Clamp(count, 1, MaxCount) : DefaultCount,
        int.TryParse(q["_offset"], out var offset) ? Math.Max(0, offset) : 0);

    private static JsonObject Bundle(HttpContext http, int total, int offset, int count, IEnumerable<(string Path, JsonObject Resource)> entries)
    {
        var baseUrl = Base(http);
        var self = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}{http.Request.QueryString}";
        string? next = null;
        if (offset + count < total)
        {
            var query = http.Request.Query.Where(p => p.Key is not ("_offset" or "_count"))
                .SelectMany(p => p.Value.Select(v => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(v ?? string.Empty)}"))
                .Append($"_count={count}").Append($"_offset={offset + count}");
            next = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}?{string.Join('&', query)}";
        }

        return FhirMapper.SearchBundle(self, next, total, entries.Select(e => ($"{baseUrl}/{e.Path}", e.Resource)));
    }
}
