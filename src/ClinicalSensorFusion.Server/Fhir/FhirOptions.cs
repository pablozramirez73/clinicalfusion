namespace ClinicalSensorFusion.Server.Fhir;

/// <summary>
/// Tetti dell'interfaccia FHIR (sezione <c>Fhir</c>). Servono a rendere il costo di una lettura prevedibile:
/// dove il tetto viene superato il server rifiuta con <c>too-costly</c> invece di restituire dati incompleti.
/// </summary>
public sealed class FhirOptions
{
    /// <summary>
    /// Rilevazioni scandite da una ricerca Observation. I filtri <c>code</c> e <c>category</c> dipendono dal
    /// riepilogo, non da colonne: per un <c>total</c> veritiero vanno lette tutte le rilevazioni del filtro SQL.
    /// </summary>
    public int MaxSessionsScanned { get; init; } = 2_000;

    /// <summary>
    /// Rilevazioni di una singola esportazione <c>$everything</c>. Più alto del precedente perché l'esportazione
    /// è per paziente e deve restare completa: oltre il tetto si rifiuta, non si tronca.
    /// </summary>
    public int MaxSessionsPerExport { get; init; } = 5_000;
}
