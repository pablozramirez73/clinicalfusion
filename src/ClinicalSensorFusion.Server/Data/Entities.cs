using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Sensors;

using System.Text.Json.Serialization;

namespace ClinicalSensorFusion.Server.Data;

[JsonConverter(typeof(JsonStringEnumConverter<Sex>))]
public enum Sex
{
    Unknown = 0,
    Female = 1,
    Male = 2,
    Other = 3,
}

/// <summary>
/// Anagrafica del paziente. L'app conosce solo <see cref="Code"/> (pseudonimo): l'associazione con
/// l'identità esiste solo sul server, dietro autenticazione, così un telefono smarrito non espone nomi.
/// </summary>
public sealed class Patient
{
    public Guid Id { get; set; }

    /// <summary>Codice pseudonimo, coincide con il "codice soggetto" inserito nell'app. Univoco, maiuscolo.</summary>
    public required string Code { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public DateOnly? BirthDate { get; set; }

    public Sex Sex { get; set; }

    public string? FiscalCode { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    /// <summary>Diagnosi / motivo della presa in carico (testo libero).</summary>
    public string? Diagnosis { get; set; }

    public string? Notes { get; set; }

    /// <summary>Creato automaticamente da una rilevazione con codice sconosciuto: anagrafica da completare.</summary>
    public bool IsPlaceholder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Concorrenza ottimistica (colonna di sistema PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; set; }

    public List<SessionEntity> Sessions { get; set; } = [];

    public static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();
}

/// <summary>Rilevazione ricevuta dall'app. Alcuni indicatori del riepilogo sono denormalizzati per le query.</summary>
public sealed class SessionEntity
{
    public required string Id { get; set; }

    public Guid PatientId { get; set; }

    public Patient? Patient { get; set; }

    public required string SubjectCode { get; set; }

    public required string OperatorId { get; set; }

    public required string OperatorName { get; set; }

    public CaptureMode Mode { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public string? Notes { get; set; }

    public string? DeviceModel { get; set; }

    /// <summary>
    /// Nome (da <c>Ingest:Devices</c>) del dispositivo che ha inviato la rilevazione: solo quello può
    /// aggiornarla. Null per le righe precedenti all'introduzione del vincolo: le adotta il primo reinvio.
    /// </summary>
    public string? OwnerDevice { get; set; }

    /// <summary><see cref="SessionSummary"/> serializzato (jsonb).</summary>
    public string? SummaryJson { get; set; }

    public double? DurationSeconds { get; set; }

    public double? LidarCoverage { get; set; }

    public double? TremorFrequencyHz { get; set; }

    public double? TremorRmsMs2 { get; set; }

    public long? Steps { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<AudioRecordingEntity> Audio { get; set; } = [];

    public List<AiAnalysisEntity> Analyses { get; set; } = [];
}

/// <summary>Blocco di campioni compresso, identico a quello del telefono (decodificato con SensorChunkCodec).</summary>
public sealed class SensorChunkEntity
{
    public required string SessionId { get; set; }

    public SensorKind Kind { get; set; }

    public int Sequence { get; set; }

    public double StartTime { get; set; }

    public double EndTime { get; set; }

    public int SampleCount { get; set; }

    public required byte[] Payload { get; set; }
}

public sealed class AudioRecordingEntity
{
    public required string Id { get; set; }

    public required string SessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public double DurationSeconds { get; set; }

    public string? FeaturesJson { get; set; }

    /// <summary>Percorso relativo nello storage audio; null finché il file non è stato caricato.</summary>
    public string? StoragePath { get; set; }

    public long? SizeBytes { get; set; }

    public DateTimeOffset? UploadedAt { get; set; }
}

public sealed class AiAnalysisEntity
{
    public required string Id { get; set; }

    public required string SessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string Model { get; set; }

    public required string Language { get; set; }

    public required string OperatorId { get; set; }

    public required string Prompt { get; set; }

    public required string Response { get; set; }
}

/// <summary>Registro degli accessi e delle modifiche (dati sanitari: chi ha visto o cambiato cosa).</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public required string Actor { get; set; }

    public required string Action { get; set; }

    public string? EntityId { get; set; }

    /// <summary>Indirizzo del client (quello originale, dietro reverse proxy); null fuori da una richiesta HTTP.</summary>
    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }
}
