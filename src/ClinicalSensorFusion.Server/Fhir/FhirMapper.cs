using System.Globalization;
using System.Text.Json.Nodes;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fhir;
using ClinicalSensorFusion.Server.Data;

namespace ClinicalSensorFusion.Server.Fhir;

/// <summary>
/// Traduzione dei dati del server in risorse HL7 FHIR R4 (4.0.1). Sola lettura: le risorse si generano al
/// momento dai dati archiviati, non c'è un archivio FHIR separato.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Patient</b>: l'anagrafica. Il codice pseudonimo e il codice fiscale sono <c>identifier</c>.</item>
/// <item><b>DiagnosticReport</b>: una rilevazione, con i riferimenti alle sue osservazioni.</item>
/// <item><b>Observation</b>: un indicatore della rilevazione. Parametri vitali (frequenza cardiaca, SpO2,
/// temperatura) e passi hanno codice LOINC e unità UCUM e dichiarano il profilo FHIR dei segni vitali;
/// escursione articolare, tremore e alzate dalla sedia non hanno un codice standard verificato e usano il
/// sistema di codifica locale <see cref="IndicatorSystem"/>, dichiarato come tale.</item>
/// </list>
/// Gli identificativi delle osservazioni sono stabili: <c>{id rilevazione}-{indicatore}</c>.
/// </remarks>
public static class FhirMapper
{
    public const string FhirVersion = "4.0.1";
    public const string Loinc = FhirObservations.Loinc;
    public const string Ucum = FhirObservations.Ucum;
    public const string CategorySystem = FhirObservations.CategorySystem;

    /// <summary>Codice fiscale italiano (OID del Ministero dell'Economia e delle Finanze).</summary>
    public const string FiscalCodeSystem = "urn:oid:2.16.840.1.113883.2.9.4.3.2";

    /// <summary>Codice pseudonimo del paziente, assegnato dalla struttura.</summary>
    public const string PatientCodeSystem = "urn:clinical-sensor-fusion:patient-code";

    /// <summary>Indicatori senza codice standard (escursione articolare, tremore, alzate dalla sedia).</summary>
    public const string IndicatorSystem = FhirObservations.IndicatorSystem;

    public static string Instant(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static JsonObject Patient(Patient p)
    {
        var resource = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["id"] = p.Id.ToString(),
            ["meta"] = new JsonObject { ["versionId"] = p.Version.ToString(CultureInfo.InvariantCulture), ["lastUpdated"] = Instant(p.UpdatedAt) },
            ["identifier"] = new JsonArray(new JsonObject { ["use"] = "usual", ["system"] = PatientCodeSystem, ["value"] = p.Code }),
            ["active"] = true,
            ["gender"] = p.Sex switch { Sex.Female => "female", Sex.Male => "male", Sex.Other => "other", _ => "unknown" },
        };

        if (!string.IsNullOrWhiteSpace(p.FiscalCode))
        {
            resource["identifier"]!.AsArray().Add(new JsonObject { ["use"] = "official", ["system"] = FiscalCodeSystem, ["value"] = p.FiscalCode });
        }

        if (!string.IsNullOrWhiteSpace(p.LastName) || !string.IsNullOrWhiteSpace(p.FirstName))
        {
            var name = new JsonObject { ["use"] = "official", ["text"] = $"{p.FirstName} {p.LastName}".Trim() };
            if (!string.IsNullOrWhiteSpace(p.LastName))
            {
                name["family"] = p.LastName;
            }

            if (!string.IsNullOrWhiteSpace(p.FirstName))
            {
                name["given"] = new JsonArray(p.FirstName);
            }

            resource["name"] = new JsonArray(name);
        }

        if (p.BirthDate is { } birth)
        {
            resource["birthDate"] = birth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var telecom = new JsonArray();
        if (!string.IsNullOrWhiteSpace(p.Phone))
        {
            telecom.Add(new JsonObject { ["system"] = "phone", ["value"] = p.Phone });
        }

        if (!string.IsNullOrWhiteSpace(p.Email))
        {
            telecom.Add(new JsonObject { ["system"] = "email", ["value"] = p.Email });
        }

        if (telecom.Count > 0)
        {
            resource["telecom"] = telecom;
        }

        if (!string.IsNullOrWhiteSpace(p.Address))
        {
            resource["address"] = new JsonArray(new JsonObject { ["text"] = p.Address });
        }

        return resource;
    }

    /// <summary>Tutte le osservazioni di una rilevazione, con la chiave che completa il loro identificativo.</summary>
    /// <summary>Indicatori clinici della rilevazione: la mappatura è nel Core, condivisa con l'app (<see cref="FhirObservations"/>).</summary>
    public static IReadOnlyList<(string Key, JsonObject Resource)> Observations(SessionEntity s, SessionSummary? summary) =>
        summary is null ? [] : [.. FhirObservations.Clinical(summary).Select(o => (o.Key, Complete(o.Body, s, o.Key)))];

    public static JsonObject DiagnosticReport(SessionEntity s, IEnumerable<string> observationIds)
    {
        var mode = s.Mode switch
        {
            CaptureMode.BodyLidar => "corpo",
            CaptureMode.HandsLidar => "mani",
            CaptureMode.FaceTrueDepth => "volto",
            _ => s.Mode.ToString(),
        };
        var report = new JsonObject
        {
            ["resourceType"] = "DiagnosticReport",
            ["id"] = s.Id,
            ["meta"] = new JsonObject { ["lastUpdated"] = Instant(s.UpdatedAt) },
            ["status"] = "final",
            ["code"] = FhirObservations.Code(IndicatorSystem, [("movement-assessment-" + FhirObservations.Kebab(s.Mode.ToString()), $"Rilevazione con sensori del telefono ({mode})")], $"Rilevazione con sensori del telefono ({mode})"),
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{s.PatientId}" },
            ["effectivePeriod"] = Period(s),
            ["issued"] = Instant(s.ReceivedAt),
            ["performer"] = new JsonArray(new JsonObject { ["display"] = s.OperatorName }),
            ["result"] = new JsonArray([.. observationIds.Select(id => (JsonNode)new JsonObject { ["reference"] = $"Observation/{id}" })]),
        };
        if (report["result"]!.AsArray().Count == 0)
        {
            report.Remove("result");
        }

        return report;
    }

    public static JsonObject OperationOutcome(string severity, string code, string diagnostics) => new()
    {
        ["resourceType"] = "OperationOutcome",
        ["issue"] = new JsonArray(new JsonObject { ["severity"] = severity, ["code"] = code, ["diagnostics"] = diagnostics }),
    };

    /// <param name="total">Numero complessivo di risultati (non solo quelli della pagina); null per una raccolta completa.</param>
    public static JsonObject SearchBundle(string selfUrl, string? nextUrl, int? total, IEnumerable<(string FullUrl, JsonObject Resource)> entries)
    {
        var link = new JsonArray(new JsonObject { ["relation"] = "self", ["url"] = selfUrl });
        if (nextUrl is not null)
        {
            link.Add(new JsonObject { ["relation"] = "next", ["url"] = nextUrl });
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["timestamp"] = Instant(DateTimeOffset.UtcNow),
        };
        if (total is { } t)
        {
            bundle["total"] = t;
        }

        bundle["link"] = link;

        // In FHIR un array vuoto non è ammesso: senza risultati l'elemento "entry" non c'è.
        var entry = new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["fullUrl"] = e.FullUrl,
            ["resource"] = e.Resource,
            ["search"] = new JsonObject { ["mode"] = "match" },
        })]);
        if (entry.Count > 0)
        {
            bundle["entry"] = entry;
        }

        return bundle;
    }

    /// <summary>Campi comuni a tutte le osservazioni; conserva il profilo già indicato in <c>meta</c>.</summary>
    private static JsonObject Complete(JsonObject body, SessionEntity s, string key)
    {
        var meta = body["meta"] as JsonObject ?? [];
        body.Remove("meta");
        meta["lastUpdated"] = Instant(s.UpdatedAt);

        var observation = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["id"] = $"{s.Id}-{key}",
            ["meta"] = meta,
            ["status"] = "final",
        };
        foreach (var (name, node) in body.ToList())
        {
            body.Remove(name);
            observation[name] = node;
        }

        observation["subject"] = new JsonObject { ["reference"] = $"Patient/{s.PatientId}" };
        observation["effectivePeriod"] = Period(s);
        observation["issued"] = Instant(s.ReceivedAt);
        observation["performer"] = new JsonArray(new JsonObject { ["display"] = s.OperatorName });
        if (!string.IsNullOrWhiteSpace(s.DeviceModel))
        {
            observation["device"] = new JsonObject { ["display"] = s.DeviceModel };
        }

        return observation;
    }

    private static JsonObject Period(SessionEntity s)
    {
        var period = new JsonObject { ["start"] = Instant(s.StartedAt) };
        if (s.EndedAt is { } end)
        {
            period["end"] = Instant(end);
        }

        return period;
    }
}
