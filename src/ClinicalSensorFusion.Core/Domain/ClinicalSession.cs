namespace ClinicalSensorFusion.Core.Domain;

/// <summary>
/// Una rilevazione clinica: intervallo di acquisizione multi-sensore associato a un soggetto
/// (identificato solo da un codice pseudonimo) e all'operatore che l'ha eseguita.
/// </summary>
public sealed class ClinicalSession
{
    public required string Id { get; init; }

    /// <summary>Codice pseudonimo del soggetto. Mai nome e cognome: il DB è cifrato ma il principio è la minimizzazione.</summary>
    public required string SubjectCode { get; set; }

    public required string OperatorId { get; init; }

    public required string OperatorName { get; init; }

    public required CaptureMode Mode { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; set; }

    public string? Notes { get; set; }

    public string? DeviceModel { get; init; }

    public SessionSummary? Summary { get; set; }

    /// <summary>Quando la rilevazione è stata ricevuta per intero dal server clinico; null se non ancora inviata.</summary>
    public DateTimeOffset? SyncedAt { get; set; }

    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;

    public static string NewId() => Guid.NewGuid().ToString("N");
}
