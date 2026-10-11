using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>
/// Registrazione audio di una rilevazione. Il file è salvato, come nell'app originale, in
/// <c>Library/audio/&lt;Id&gt;/audio.caf</c> (CAF, mono, 48 kHz, Float32 non compresso).
/// </summary>
public sealed class AudioRecording
{
    public const int SampleRate = 48_000;
    public const int Channels = 1;
    public const string FileName = "audio.caf";

    /// <summary>Identificativo casuale di 32 caratteri esadecimali (formato dell'app originale).</summary>
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public double DurationSeconds { get; set; }

    public AudioFeatures? Features { get; set; }

    /// <summary>Percorso relativo alla cartella Library dell'app.</summary>
    public string RelativePath => Path.Combine("audio", Id, FileName);

    public static string NewId() => Guid.NewGuid().ToString("N");
}
