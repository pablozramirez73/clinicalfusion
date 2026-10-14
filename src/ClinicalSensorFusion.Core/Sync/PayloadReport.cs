using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.Core.Sync;

/// <summary>Quanti dati dei sensori accompagnano il JSON della rilevazione.</summary>
public sealed record ChunkStats(int Chunks, long Samples, long Bytes);

/// <summary>
/// Controllo del payload JSON della rilevazione, cioè esattamente il documento che <see cref="ClinicalSyncClient"/>
/// invia al server: dimensione, impronta, rilettura e completezza.
/// </summary>
/// <param name="Sha256">Impronta SHA-256 del JSON: la stessa sul server significa che è arrivato identico.</param>
/// <param name="RoundTrips">Il JSON si rilegge e, riscritto, è identico byte per byte.</param>
/// <param name="MissingFields">Campi obbligatori vuoti.</param>
/// <param name="NonFiniteNumbers">
/// Valori NaN o Infinito: il server .NET li accetta, ma non sono JSON standard e altri lettori li rifiutano.
/// </param>
public sealed record PayloadReport(int Bytes, string Sha256, bool RoundTrips, IReadOnlyList<string> MissingFields, int NonFiniteNumbers, ChunkStats Chunks)
{
    public const string Section = "Payload JSON";

    public bool IsValid => RoundTrips && MissingFields.Count == 0;

    public static PayloadReport Inspect(SessionUpload upload, ChunkStats chunks)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(upload, SyncJsonContext.Default.SessionUpload);

        var roundTrips = false;
        try
        {
            var back = JsonSerializer.Deserialize(json, SyncJsonContext.Default.SessionUpload);
            roundTrips = back is not null && JsonSerializer.SerializeToUtf8Bytes(back, SyncJsonContext.Default.SessionUpload).AsSpan().SequenceEqual(json);
        }
        catch (JsonException)
        {
            // Resta false: il documento non si rilegge.
        }

        var missing = new List<string>();
        void Require(string name, bool present)
        {
            if (!present)
            {
                missing.Add(name);
            }
        }

        Require(Loc.N("identificativo"), !string.IsNullOrWhiteSpace(upload.Id));
        Require(Loc.N("codice soggetto"), !string.IsNullOrWhiteSpace(upload.SubjectCode));
        Require(Loc.N("operatore"), !string.IsNullOrWhiteSpace(upload.OperatorId) && !string.IsNullOrWhiteSpace(upload.OperatorName));
        Require(Loc.N("fine rilevazione"), upload.EndedAt is not null);
        Require(Loc.N("riepilogo"), upload.Summary is not null);

        return new PayloadReport(
            json.Length,
            Convert.ToHexStringLower(SHA256.HashData(json)),
            roundTrips,
            missing,
            CountNonFinite(json),
            chunks);
    }

    public IReadOnlyList<SummaryRow> ToRows(CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        return
        [
            new(Section, Loc.T("Esito"), IsValid ? Loc.T("valido") : Loc.T("da verificare"), !IsValid),
            new(Section, Loc.T("Dimensione"), string.Format(c, "{0:N1} kB", Bytes / 1024.0)),
            new(Section, Loc.T("Impronta SHA-256"), Sha256[..12] + "…"),
            new(Section, Loc.T("Rilettura"), RoundTrips ? Loc.T("identica") : Loc.T("non riuscita"), !RoundTrips),
            new(Section, Loc.T("Campi obbligatori"), MissingFields.Count == 0 ? Loc.T("completi") : Loc.F("mancano: {0}", string.Join(", ", MissingFields.Select(f => Loc.T(f)))), MissingFields.Count > 0),
            new(Section, Loc.T("Valori non numerici"), NonFiniteNumbers == 0 ? Loc.T("nessuno") : string.Format(c, Loc.T("{0} (NaN o infinito)"), NonFiniteNumbers), NonFiniteNumbers > 0),
            new(Section, Loc.T("Dati dei sensori"), string.Format(c, Loc.T("{0:N0} blocchi · {1:N0} campioni · {2:N1} MB"), Chunks.Chunks, Chunks.Samples, Chunks.Bytes / 1_048_576.0), Chunks.Chunks == 0),
        ];
    }

    /// <summary>Conta i letterali "NaN", "Infinity" e "-Infinity" usati come valori (non dentro altri testi).</summary>
    private static int CountNonFinite(ReadOnlySpan<byte> json)
    {
        var count = 0;
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is "NaN" or "Infinity" or "-Infinity")
            {
                count++;
            }
        }

        return count;
    }
}
