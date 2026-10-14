using System.Globalization;
using System.Text.Json.Nodes;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Sensors;
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
    public const string Loinc = "http://loinc.org";
    public const string Ucum = "http://unitsofmeasure.org";
    public const string CategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";

    /// <summary>Codice fiscale italiano (OID del Ministero dell'Economia e delle Finanze).</summary>
    public const string FiscalCodeSystem = "urn:oid:2.16.840.1.113883.2.9.4.3.2";

    /// <summary>Codice pseudonimo del paziente, assegnato dalla struttura.</summary>
    public const string PatientCodeSystem = "urn:clinical-sensor-fusion:patient-code";

    /// <summary>Indicatori senza codice standard (escursione articolare, tremore, alzate dalla sedia).</summary>
    public const string IndicatorSystem = "urn:clinical-sensor-fusion:indicator";

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

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
    public static IReadOnlyList<(string Key, JsonObject Resource)> Observations(SessionEntity s, SessionSummary? summary)
    {
        var result = new List<(string, JsonObject)>();
        if (summary is null)
        {
            return result;
        }

        void Add(string key, JsonObject observation) => result.Add((key, Complete(observation, s, key)));

        if (Channel(summary, SensorKind.HeartRate, "Frequenza cardiaca") is { } heartRate)
        {
            Add("heart-rate", Vital("heartrate", [("8867-4", "Heart rate")], heartRate, "beats/minute", "/min", "Sensore Bluetooth di frequenza cardiaca"));
        }

        if (Channel(summary, SensorKind.PulseOximeter, "SpO2") is { } spo2)
        {
            Add("spo2", Vital(
                "oxygensat",
                [("2708-6", "Oxygen saturation in Arterial blood"), ("59408-5", "Oxygen saturation in Arterial blood by Pulse oximetry")],
                spo2, "%", "%", "Saturimetro Bluetooth"));
        }

        if (Channel(summary, SensorKind.BodyTemperature, "Temperatura") is { } temperature)
        {
            Add("body-temperature", Vital("bodytemp", [("8310-5", "Body temperature")], temperature, "Cel", "Cel", "Termometro Bluetooth"));
        }

        if (summary.Gait is { } gait)
        {
            Add("steps", new JsonObject
            {
                ["category"] = Category("activity", "Activity"),
                ["code"] = Code(Loinc, [("55423-8", "Number of steps in unspecified time Pedometer")], "Passi"),
                ["valueQuantity"] = Quantity(gait.Steps, "steps", "{steps}"),
                ["method"] = new JsonObject { ["text"] = "Pedometro del telefono" },
            });
        }

        foreach (var (joint, range) in summary.JointRanges.OrderBy(j => j.Key))
        {
            var name = SummaryReport.JointName(joint);
            Add("rom-" + Kebab(joint.ToString()), new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Code(IndicatorSystem, [("rom-" + Kebab(joint.ToString()), $"Escursione articolare: {name}")], $"Escursione articolare: {name}"),
                ["valueQuantity"] = Quantity(range.RangeOfMotion, "deg", "deg"),
                ["method"] = new JsonObject { ["text"] = "Stima ottica (MediaPipe, con profondità LiDAR se attiva)" },
                ["note"] = new JsonArray(new JsonObject { ["text"] = $"Stima ottica con incertezza di ±5–10°, su {range.Samples} fotogrammi." }),
                ["component"] = new JsonArray(
                    Component("angle-min", "Angolo minimo", range.Min, "deg", "deg"),
                    Component("angle-max", "Angolo massimo", range.Max, "deg", "deg"),
                    Component("angle-mean", "Angolo medio", range.Mean, "deg", "deg")),
            });
        }

        if (summary.Tremor is { IsSignificant: true } tremor)
        {
            Add("tremor", new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Code(IndicatorSystem, [("tremor-frequency", "Frequenza dominante del tremore (3–12 Hz)")], "Frequenza dominante del tremore"),
                ["valueQuantity"] = Quantity(tremor.DominantFrequencyHz, "Hz", "Hz"),
                ["method"] = new JsonObject { ["text"] = "Accelerometro del telefono, banda 3–12 Hz" },
                ["component"] = new JsonArray(Component("tremor-amplitude-rms", "Ampiezza RMS in banda", tremor.BandRmsMs2, "m/s2", "m/s2")),
            });
        }

        if (summary.SitToStandCount is { } rises)
        {
            Add("sit-to-stand", new JsonObject
            {
                ["category"] = Category("exam", "Exam"),
                ["code"] = Code(IndicatorSystem, [("sit-to-stand-count", "Alzate dalla sedia contate")], "Alzate dalla sedia"),
                ["valueInteger"] = rises,
                ["method"] = new JsonObject { ["text"] = "Conteggio automatico dalla flessione del ginocchio" },
            });
        }

        return result;
    }

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
            ["code"] = Code(IndicatorSystem, [("movement-assessment-" + Kebab(s.Mode.ToString()), $"Rilevazione con sensori del telefono ({mode})")], $"Rilevazione con sensori del telefono ({mode})"),
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

    private static ClinicalSensorFusion.Core.Recording.ChannelStats? Channel(SessionSummary summary, SensorKind kind, string name) =>
        summary.Sensors is { } sensors && sensors.TryGetValue(kind, out var stream) ? stream.Channels.FirstOrDefault(c => c.Name == name) : null;

    /// <summary>Segno vitale secondo il profilo FHIR: valore = media sulla rilevazione, minimo e massimo nella nota.</summary>
    private static JsonObject Vital(string profile, (string Code, string Display)[] codes, ClinicalSensorFusion.Core.Recording.ChannelStats stats, string unit, string ucum, string method) => new()
    {
        ["meta"] = new JsonObject { ["profile"] = new JsonArray($"http://hl7.org/fhir/StructureDefinition/{profile}") },
        ["category"] = Category("vital-signs", "Vital Signs"),
        ["code"] = Code(Loinc, codes, codes[0].Display),
        ["valueQuantity"] = Quantity(stats.Mean, unit, ucum),
        ["method"] = new JsonObject { ["text"] = method },
        ["note"] = new JsonArray(new JsonObject
        {
            ["text"] = string.Format(Italian, "Media di {0} misure durante la rilevazione (minimo {1:0.#}, massimo {2:0.#}).", stats.Samples, stats.Min, stats.Max),
        }),
    };

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

    private static JsonArray Category(string code, string display) => new(new JsonObject
    {
        ["coding"] = new JsonArray(new JsonObject { ["system"] = CategorySystem, ["code"] = code, ["display"] = display }),
    });

    private static JsonObject Code(string system, (string Code, string Display)[] codes, string text) => new()
    {
        ["coding"] = new JsonArray([.. codes.Select(c => (JsonNode)new JsonObject { ["system"] = system, ["code"] = c.Code, ["display"] = c.Display })]),
        ["text"] = text,
    };

    private static JsonObject Quantity(double value, string unit, string ucum) => new()
    {
        ["value"] = Math.Round(value, 3),
        ["unit"] = unit,
        ["system"] = Ucum,
        ["code"] = ucum,
    };

    private static JsonObject Component(string code, string display, double value, string unit, string ucum) => new()
    {
        ["code"] = Code(IndicatorSystem, [(code, display)], display),
        ["valueQuantity"] = Quantity(value, unit, ucum),
    };

    /// <summary><c>RightKneeFlexion</c> → <c>right-knee-flexion</c>.</summary>
    private static string Kebab(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
