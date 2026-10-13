namespace ClinicalSensorFusion.Core.Fusion;

/// <summary>
/// Raddrizza una sequenza di pose 3D per la visualizzazione. I punti sono archiviati nel sistema della camera,
/// cioè dell'immagine del sensore: con il telefono in verticale il sensore è orizzontale e il soggetto in piedi
/// risulta coricato su un fianco. La rotazione attorno all'asse ottico (0°, 90°, 180° o 270°) si ricava dai
/// dati stessi: è quella che porta il tronco, in media, con le spalle sopra le anche.
/// </summary>
/// <remarks>
/// Vale per soggetti in piedi o seduti, come nei test previsti. Un soggetto realmente disteso verrebbe
/// mostrato in piedi: gli angoli articolari non cambiano (non dipendono dall'orientamento), solo la vista.
/// </remarks>
public static class PoseUpright
{
    /// <summary>Sotto questa lunghezza media (rispetto a un tronco sempre concorde) la direzione non è affidabile.</summary>
    private const double MinAgreement = 0.3;

    /// <summary>
    /// Quarti di giro (0–3) da applicare con <see cref="Rotate"/> perché l'asse Y (verso il basso) coincida
    /// con la verticale del soggetto. 0 se i dati non bastano a deciderlo.
    /// </summary>
    /// <param name="poses">Fotogrammi di 33 punti × [x, y, z]; NaN per i punti mancanti.</param>
    public static int QuarterTurns(IEnumerable<float[]> poses)
    {
        double sumX = 0, sumY = 0;
        var frames = 0;
        foreach (var p in poses)
        {
            if (p.Length < PoseLandmark.Count * 3)
            {
                continue;
            }

            // Dalle anche alle spalle: il tronco è il segmento più visibile e resta verticale anche nello squat.
            var x = Mid(p, PoseLandmark.LeftShoulder, PoseLandmark.RightShoulder, 0) - Mid(p, PoseLandmark.LeftHip, PoseLandmark.RightHip, 0);
            var y = Mid(p, PoseLandmark.LeftShoulder, PoseLandmark.RightShoulder, 1) - Mid(p, PoseLandmark.LeftHip, PoseLandmark.RightHip, 1);
            var length = Math.Sqrt((x * x) + (y * y));
            if (!double.IsFinite(length) || length < 1e-6)
            {
                continue;
            }

            sumX += x / length;
            sumY += y / length;
            frames++;
        }

        if (frames == 0 || Math.Sqrt((sumX * sumX) + (sumY * sumY)) / frames < MinAgreement)
        {
            return 0;
        }

        // "Su" deve diventare −Y (Y cresce verso il basso).
        if (Math.Abs(sumY) >= Math.Abs(sumX))
        {
            return sumY < 0 ? 0 : 2;
        }

        return sumX > 0 ? 1 : 3;
    }

    /// <summary>Ruota sul posto i punti [x, y, z] di <paramref name="quarterTurns"/> quarti di giro attorno a Z.</summary>
    public static void Rotate(Span<float> points, int quarterTurns)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        if (quarterTurns == 0)
        {
            return;
        }

        for (var i = 0; i + 2 < points.Length; i += 3)
        {
            var (x, y) = (points[i], points[i + 1]);
            // Rotazioni proprie (determinante +1): destra e sinistra del soggetto non si scambiano.
            (points[i], points[i + 1]) = quarterTurns switch
            {
                1 => (y, -x),
                2 => (-x, -y),
                _ => (-y, x),
            };
        }
    }

    private static double Mid(float[] p, int a, int b, int axis) => (p[(a * 3) + axis] + p[(b * 3) + axis]) / 2.0;
}
