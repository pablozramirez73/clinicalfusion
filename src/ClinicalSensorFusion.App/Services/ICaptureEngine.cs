using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Motore di acquisizione di piattaforma: ARKit (camera, LiDAR, TrueDepth), MediaPipe, CoreMotion,
/// microfono e sensori di sistema. Su iOS: <c>Platforms/iOS/Services/IosCaptureEngine</c>.
/// </summary>
/// <remarks>
/// Due fasi distinte: l'anteprima (solo camera + visione, nessun dato salvato) e la registrazione
/// (tutti i sensori verso il sink). Così l'operatore inquadra il paziente prima di iniziare e nulla
/// viene memorizzato senza un'azione esplicita.
/// </remarks>
public interface ICaptureEngine
{
    bool IsLidarSupported { get; }

    /// <summary>
    /// Profondità LiDAR accesa su richiesta dell'operatore (solo modalità corpo). Spenta, gli angoli
    /// usano la stima 3D di MediaPipe. Ha effetto al prossimo avvio dell'anteprima.
    /// </summary>
    bool UseLidar { get; set; }

    bool IsFaceTrackingSupported { get; }

    bool IsRecording { get; }

    /// <summary>Secondi dall'avvio del sistema: base tempi dei timestamp nativi dei sensori.</summary>
    double SystemUptime { get; }

    /// <summary>Stato dei singoli sensori (per la schermata di acquisizione).</summary>
    IReadOnlyList<SensorStatus> GetSensorStatus();

    /// <summary>Punti e scheletro da disegnare sull'anteprima. Sollevato su un thread di background.</summary>
    event EventHandler<OverlayFrame>? OverlayUpdated;

    Task<bool> RequestPermissionsAsync(bool includeLocation);

    Task StartPreviewAsync(CaptureMode mode);

    Task StopPreviewAsync();

    Task StartRecordingAsync(ClinicalSession session, ISensorSink sink, SensorClock clock, bool includeLocation);

    /// <summary>Ferma tutte le sorgenti e restituisce la registrazione audio (null se il microfono non era disponibile).</summary>
    Task<AudioRecording?> StopRecordingAsync();
}

public sealed record SensorStatus(string Name, bool Available, bool Running);

/// <summary>
/// Punti da disegnare, in coordinate normalizzate della vista di anteprima (0..1, origine in alto a sinistra),
/// già trasformate con la displayTransform di ARKit.
/// </summary>
public sealed record OverlayFrame(
    IReadOnlyList<OverlayPoint> Points,
    IReadOnlyList<(int From, int To)> Connections,
    string? Caption,
    double InferenceMs);

public readonly record struct OverlayPoint(float X, float Y, bool Valid, OverlayPointKind Kind);

public enum OverlayPointKind
{
    /// <summary>Punto con profondità LiDAR valida.</summary>
    Lidar,

    /// <summary>Punto solo 2D (profondità non disponibile).</summary>
    Image,

    Hand,

    Face,
}
