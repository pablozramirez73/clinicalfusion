namespace ClinicalSensorFusion.Core.Domain;

/// <summary>Configurazione di acquisizione scelta dall'operatore all'avvio della rilevazione.</summary>
public enum CaptureMode
{
    /// <summary>Fotocamera posteriore + LiDAR, MediaPipe Pose: postura, ROM articolare, cammino.</summary>
    BodyLidar = 0,

    /// <summary>Fotocamera posteriore + LiDAR, MediaPipe Hands: motricità fine, tremore della mano.</summary>
    HandsLidar = 1,

    /// <summary>Fotocamera frontale TrueDepth, MediaPipe Face + blendshape ARKit: mimica facciale.</summary>
    FaceTrueDepth = 2,
}
