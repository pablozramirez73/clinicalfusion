namespace ClinicalSensorFusion.Core.Fusion;

/// <summary>
/// Orientamento passato a MediaPipe (stessa semantica di UIImage.Orientation): indica come ruotare
/// l'immagine del sensore per vederla dritta. Con l'iPhone in verticale la camera posteriore produce
/// immagini landscape che vanno ruotate di 90° in senso orario → <see cref="Right"/>.
/// </summary>
public enum ImageOrientation
{
    Up = 0,
    Down = 1,
    Left = 2,
    Right = 3,
}

/// <summary>
/// Conversione delle coordinate normalizzate tra immagine "orientata" (quella su cui MediaPipe
/// restituisce i punti) e immagine nativa del sensore (quella in cui sono espressi profondità LiDAR e intrinseci).
/// </summary>
public static class OrientationMapper
{
    /// <summary>Da coordinate orientate (u, v) a coordinate del sensore (x, y).</summary>
    public static (float X, float Y) ToSensor(float u, float v, ImageOrientation orientation) => orientation switch
    {
        ImageOrientation.Up => (u, v),
        // Sensore ruotato di 90° orario per ottenere l'immagine orientata: u = 1 − y, v = x.
        ImageOrientation.Right => (v, 1f - u),
        // Ruotato di 90° antiorario: u = y, v = 1 − x.
        ImageOrientation.Left => (1f - v, u),
        ImageOrientation.Down => (1f - u, 1f - v),
        _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, null),
    };

    /// <summary>Da coordinate del sensore (x, y) a coordinate orientate (u, v). Inversa di <see cref="ToSensor"/>.</summary>
    public static (float U, float V) ToOriented(float x, float y, ImageOrientation orientation) => orientation switch
    {
        ImageOrientation.Up => (x, y),
        ImageOrientation.Right => (1f - y, x),
        ImageOrientation.Left => (y, 1f - x),
        ImageOrientation.Down => (1f - x, 1f - y),
        _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, null),
    };
}
