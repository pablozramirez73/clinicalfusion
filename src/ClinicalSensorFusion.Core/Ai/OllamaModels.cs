using System.Text.Json.Serialization;

namespace ClinicalSensorFusion.Core.Ai;

public sealed record OllamaTagsResponse([property: JsonPropertyName("models")] List<OllamaModelInfo> Models);

public sealed record OllamaModelInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt);

public sealed record OllamaMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<OllamaMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("options")] OllamaOptions? Options = null,
    [property: JsonPropertyName("keep_alive")] string? KeepAlive = null);

/// <summary>Temperatura bassa: per un riepilogo clinico si vuole un testo ripetibile, non creativo.</summary>
public sealed record OllamaOptions(
    [property: JsonPropertyName("temperature")] double Temperature = 0.2,
    [property: JsonPropertyName("num_ctx")] int? ContextLength = 8192);

public sealed record OllamaChatChunk(
    [property: JsonPropertyName("message")] OllamaMessage? Message,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("error")] string? Error);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatChunk))]
internal sealed partial class OllamaJsonContext : JsonSerializerContext;
