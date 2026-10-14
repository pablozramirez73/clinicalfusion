using System.Globalization;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>Giudizio sintetico sull'affidabilità tecnica di una rilevazione.</summary>
public enum ReliabilityLevel
{
    Low,
    Medium,
    High,
}

/// <param name="Score">Da 0 a 100: 100 meno le penalità dei fattori fuori soglia.</param>
/// <param name="Factors">Fattori controllati, con il valore osservato; <c>Warning</c> se hanno tolto punti.</param>
public sealed record ReliabilityReport(int Score, ReliabilityLevel Level, IReadOnlyList<SummaryRow> Factors);

/// <summary>
/// Affidabilità della misura: quanto i dati raccolti sono adatti a sostenere gli indicatori calcolati.
/// È un controllo tecnico dell'acquisizione (campionamento, perdite, inquadratura), non un giudizio clinico
/// né una stima dell'errore dei singoli valori. Soglie e penalità sono scelte di progetto, non validate.
/// </summary>
public static class MeasurementReliability
{
    /// <summary>Titolo della sezione in italiano, cioè la chiave di traduzione: le righe lo portano tradotto.</summary>
    public const string Section = "Affidabilità della misura";

    public static ReliabilityReport Assess(SessionSummary s, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var factors = new List<SummaryRow>();
        var penalty = 0;

        void Add(string label, string value, int points)
        {
            penalty += points;
            factors.Add(new SummaryRow(Loc.T(Section), Loc.T(label), value, points > 0));
        }

        var duration = s.DurationSeconds;
        Add(Loc.N("Durata utile"), string.Format(c, "{0:F0} s", duration), duration < 5 ? 20 : 0);

        // Sensori di movimento: attesi 100 campioni al secondo.
        var motion = Rate(s, SensorKind.Motion);
        Add(Loc.N("Sensori di movimento"), motion is { } m ? string.Format(c, Loc.T("{0:F0} campioni/s su 100"), m) : Loc.T("assenti"),
            motion switch { null => 30, < 50 => 30, < 90 => 15, _ => 0 });

        // Visione: fotogrammi analizzati da MediaPipe (o volto ARKit) al secondo.
        var (visionKind, visionName) = s.Mode switch
        {
            CaptureMode.HandsLidar => (SensorKind.HandLandmarks, Loc.N("Riconoscimento delle mani")),
            CaptureMode.FaceTrueDepth => (SensorKind.FaceBlendshapes, Loc.N("Tracciamento del volto")),
            _ => (SensorKind.JointAngles, Loc.N("Riconoscimento del corpo")),
        };
        var vision = Rate(s, visionKind);
        Add(visionName, vision is { } v ? string.Format(c, Loc.T("{0:F0} fotogrammi/s"), v) : Loc.T("nessun fotogramma"),
            vision switch { null => 40, < 8 => 30, < 15 => 15, _ => 0 });

        var total = s.SampleCounts.Values.Sum();
        var lost = total + s.DroppedSamples > 0 ? s.DroppedSamples / (double)(total + s.DroppedSamples) : 0;
        Add(Loc.N("Campioni persi"), string.Format(c, "{0:P1}", lost), lost switch { > 0.05 => 30, > 0.01 => 15, > 0 => 5, _ => 0 });

        if (s.Mode == CaptureMode.BodyLidar && s.MedianSubjectDistanceMeters is { } distance)
        {
            Add(Loc.N("Distanza del soggetto"), string.Format(c, Loc.T("{0:F1} m (ideale 1–4,5)"), distance), distance is < 1 or > 4.5 ? 10 : 0);
        }

        // La copertura LiDAR conta solo se il LiDAR era acceso: senza LiDAR è assente (zero nei riepiloghi meno recenti).
        if (s.LidarCoverage is { } coverage && coverage > 0)
        {
            Add(Loc.N("Copertura LiDAR"), string.Format(c, "{0:P0}", coverage), coverage < 0.6 ? 10 : 0);
        }

        if (s.Audio is { } audio)
        {
            Add(Loc.N("Saturazione audio"), string.Format(c, "{0:P2}", audio.ClippingRatio), audio.ClippingRatio > 0.001 ? 5 : 0);
        }

        if (s.Test is { Estimated: true })
        {
            Add(Loc.N("Punteggio del test"), Loc.T("stima automatica da verificare"), 5);
        }

        var score = Math.Clamp(100 - penalty, 0, 100);
        var level = score >= 85 ? ReliabilityLevel.High : score >= 65 ? ReliabilityLevel.Medium : ReliabilityLevel.Low;
        var label = level switch { ReliabilityLevel.High => Loc.T("alta"), ReliabilityLevel.Medium => Loc.T("media"), _ => Loc.T("bassa") };
        factors.Insert(0, new SummaryRow(Loc.T(Section), Loc.T("Punteggio"), string.Format(c, "{0}/100 · {1}", score, label), level != ReliabilityLevel.High));
        return new ReliabilityReport(score, level, factors);
    }

    private static double? Rate(SessionSummary s, SensorKind kind) =>
        s.DurationSeconds > 0 && s.SampleCounts.TryGetValue(kind, out var count) && count > 0 ? count / s.DurationSeconds : null;
}
