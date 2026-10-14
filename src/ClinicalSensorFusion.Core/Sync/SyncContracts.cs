using System.Text.Json.Serialization;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Sync;

// Contratto di sincronizzazione app → server (API /api/v1/ingest). Condiviso dai due lati per costruzione:
// un cambiamento incompatibile richiede una nuova versione dell'API, non una modifica di questi tipi.

/// <summary>Metadati di una rilevazione con riepilogo, analisi IA e indice delle registrazioni audio.</summary>
public sealed record SessionUpload(
    string Id,
    string SubjectCode,
    string OperatorId,
    string OperatorName,
    CaptureMode Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Notes,
    string? DeviceModel,
    SessionSummary? Summary,
    List<AudioUpload> Audio,
    List<AnalysisUpload> Analyses);

public sealed record AudioUpload(string Id, DateTimeOffset CreatedAt, double DurationSeconds, AudioFeatures? Features);

public sealed record AnalysisUpload(
    string Id,
    DateTimeOffset CreatedAt,
    string Model,
    string Language,
    string OperatorId,
    string Prompt,
    string Response);

/// <summary>Blocco di campioni così come salvato sul telefono (payload Brotli, vedi <see cref="Recording.SensorChunkCodec"/>).</summary>
public sealed record ChunkUpload(SensorKind Kind, int Sequence, double StartTime, double EndTime, int SampleCount, byte[] Payload);

public sealed record ChunkBatch(List<ChunkUpload> Chunks);

/// <summary>Esito di un invio di blocchi: quelli già presenti sono ignorati (idempotenza).</summary>
public sealed record ChunkBatchResult(int Received, int Inserted);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionUpload))]
[JsonSerializable(typeof(ChunkBatch))]
[JsonSerializable(typeof(ChunkBatchResult))]
public sealed partial class SyncJsonContext : JsonSerializerContext;
