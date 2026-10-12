using System.Text.Json.Serialization;

namespace ClinicalSensorFusion.Core.Sensors;

/// <summary>
/// Flussi di dati registrati durante una rilevazione.
/// Il layout di <see cref="SensorReading.Values"/> per ciascun flusso è documentato qui ed è
/// parte del formato di archivio: NON cambiare l'ordine dei valori senza una migrazione.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SensorKind>))]
public enum SensorKind
{
    /// <summary>CMDeviceMotion @100 Hz: [ax, ay, az (g, accelerazione utente), gx, gy, gz (rad/s),
    /// qx, qy, qz, qw (assetto), mx, my, mz (µT, campo calibrato), gravX, gravY, gravZ (g)].</summary>
    Motion = 1,

    /// <summary>CMAltimeter: [altitudine relativa (m), pressione (kPa)].</summary>
    Barometer = 2,

    /// <summary>CMPedometer: [passi, distanza (m), cadenza (passi/s), piani saliti, piani scesi].
    /// Android: solo i passi (distanza e cadenza NaN, piani 0).</summary>
    Pedometer = 3,

    /// <summary>CLLocation: [lat, lon, altitudine (m), accuratezza orizzontale (m), velocità (m/s)].</summary>
    Location = 4,

    /// <summary>Sensore di prossimità: [0 = lontano, 1 = vicino].</summary>
    Proximity = 5,

    /// <summary>Stima luce ambientale da ARKit: [intensità (lumen), temperatura colore (K)].
    /// Android: sensore di luce, [illuminamento (lux), NaN].</summary>
    AmbientLight = 6,

    /// <summary>Stato del dispositivo: [stato termico 0..3, livello batteria 0..1].</summary>
    DeviceState = 7,

    /// <summary>LiDAR, riepilogo per fotogramma: [profondità mediana al centro (m), frazione di pixel validi].</summary>
    LidarDepth = 8,

    /// <summary>Microfono, per buffer: [RMS (dBFS), picco (0..1)]. Il segnale completo è nel file CAF.</summary>
    AudioLevel = 9,

    /// <summary>MediaPipe Pose, 33 punti × [x, y (normalizzati, orientamento sensore), visibilità].</summary>
    PoseLandmarks2D = 20,

    /// <summary>Pose fusa con LiDAR, 33 punti × [X, Y, Z] in metri (sistema camera), NaN se profondità non valida.</summary>
    PoseLandmarks3D = 21,

    /// <summary>Angoli articolari (gradi) nell'ordine di <see cref="Fusion.Joint"/> (NaN se non calcolabile),
    /// seguiti da [sorgente 3D (0 = LiDAR, 1 = MediaPipe world), distanza del soggetto (m)].</summary>
    JointAngles = 22,

    /// <summary>MediaPipe Hands: [n mani, poi per mano: lato (0 = sx, 1 = dx), profondità LiDAR del polso (m),
    /// 21 punti × [x, y (normalizzati, orientamento sensore), z relativo]].</summary>
    HandLandmarks = 23,

    /// <summary>Blendshape facciali (ARKit TrueDepth): 52 coefficienti 0..1 in ordine alfabetico dei nomi ARKit
    /// (eyeBlinkLeft, jawOpen, ...). Android: da MediaPipe Face, vedi <see cref="Fusion.FaceBlendshapeLayout"/>.</summary>
    FaceBlendshapes = 24,
}
