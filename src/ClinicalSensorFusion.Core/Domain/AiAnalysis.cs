namespace ClinicalSensorFusion.Core.Domain;

/// <summary>Risposta del modello linguistico (Ollama) su una rilevazione. Si conserva il prompt per tracciabilità.</summary>
public sealed class AiAnalysis
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string Model { get; init; }

    public required string Language { get; init; }

    public required string Prompt { get; init; }

    public required string Response { get; set; }

    public required string OperatorId { get; init; }
}
