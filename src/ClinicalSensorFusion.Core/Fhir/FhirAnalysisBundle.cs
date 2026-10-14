using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicalSensorFusion.Core.Domain;

namespace ClinicalSensorFusion.Core.Fhir;

/// <summary>Versione di HL7 FHIR dichiarata per i dati inviati al modello linguistico.</summary>
public enum FhirRelease
{
    /// <summary>FHIR 4.0.1, la stessa delle API del server.</summary>
    R4,

    /// <summary>FHIR 5.0.0.</summary>
    R5,
}

/// <summary>
/// La rilevazione come <c>Bundle</c> HL7 FHIR, nel formato JSON che l'app invia al modello linguistico:
/// un <c>DiagnosticReport</c> e le sue <c>Observation</c> (vedi <see cref="FhirObservations"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Anonimo per costruzione: nessuna risorsa <c>Patient</c>, nessun <c>subject</c> né <c>performer</c>,
/// nessuna data o ora, nessuna posizione. Anche se il server Ollama è locale può essere raggiunto da
/// Internet, e i suoi log non sono sotto il controllo dell'app.</item>
/// <item>R4 e R5: per queste risorse le due versioni hanno la stessa struttura. L'unica differenza è dove
/// stanno i limiti di misura del rapporto: in R5 sono note (<c>DiagnosticReport.note</c>, che in R4 non
/// esiste); in R4 stanno nel testo di <c>conclusion</c>.</item>
/// </list>
/// </remarks>
public static class FhirAnalysisBundle
{
    private const string Limits =
        "Angoli stimati otticamente (incertezza ±5–10°). Il tremore dipende da come è tenuto il telefono. "
        + "Non è la misura di un dispositivo medico certificato.";

    public static string VersionOf(FhirRelease release) => release == FhirRelease.R5 ? "5.0.0" : "4.0.1";

    public static JsonObject Build(ClinicalSession session, FhirRelease release = FhirRelease.R4)
    {
        ArgumentNullException.ThrowIfNull(session.Summary);
        var summary = session.Summary.WithoutLocation();

        var observations = FhirObservations.Clinical(summary).Concat(FhirObservations.Context(summary))
            .Select(o => (Url: Urn("Observation/" + o.Key), Resource: Observation(o.Key, o.Body)))
            .ToList();

        var mode = ClinicalSensorFusion.Core.Ai.ClinicalPromptBuilder.Describe(session.Mode);
        var report = new JsonObject
        {
            ["resourceType"] = "DiagnosticReport",
            ["id"] = "report",
            ["status"] = "final",
            ["code"] = FhirObservations.Code(
                FhirObservations.IndicatorSystem,
                [("movement-assessment-" + FhirObservations.Kebab(session.Mode.ToString()), $"Rilevazione con sensori del telefono ({mode})")],
                $"Rilevazione con sensori del telefono ({mode})"),
            ["result"] = new JsonArray([.. observations.Select(o => (JsonNode)new JsonObject { ["reference"] = o.Url })]),
        };
        if (release == FhirRelease.R5)
        {
            report["note"] = new JsonArray(new JsonObject { ["text"] = Limits });
        }
        else
        {
            report["conclusion"] = Limits;
        }

        var entries = new JsonArray(Entry(Urn("DiagnosticReport/report"), report));
        foreach (var (url, resource) in observations)
        {
            entries.Add((JsonNode)Entry(url, resource));
        }

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "collection",
            ["entry"] = entries,
        };
    }

    /// <summary>Il Bundle come testo JSON indentato.</summary>
    public static string ToJson(ClinicalSession session, FhirRelease release = FhirRelease.R4) =>
        Build(session, release).ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static JsonObject Observation(string key, JsonObject body)
    {
        var observation = new JsonObject { ["resourceType"] = "Observation", ["id"] = key };
        if (body["meta"] is JsonObject meta)
        {
            body.Remove("meta");
            observation["meta"] = meta;
        }

        observation["status"] = "final";
        foreach (var (name, node) in body.ToList())
        {
            body.Remove(name);
            observation[name] = node;
        }

        return observation;
    }

    private static JsonObject Entry(string fullUrl, JsonObject resource) => new() { ["fullUrl"] = fullUrl, ["resource"] = resource };

    /// <summary>
    /// In un Bundle le risorse si citano per <c>fullUrl</c>. L'UUID deriva dal nome della risorsa: lo stesso
    /// riepilogo produce sempre lo stesso documento, senza identificativi della rilevazione.
    /// </summary>
    private static string Urn(string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("clinical-sensor-fusion:" + name)).AsSpan(0, 16).ToArray();
        return "urn:uuid:" + new Guid(bytes).ToString("D");
    }
}
