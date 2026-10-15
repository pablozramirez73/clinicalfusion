using ClinicalSensorFusion.Core.Localization;
using System.Globalization;
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
    public static ReliabilityReport Assess(SessionSummary s, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var factors = new List<SummaryRow>();
        var penalty = 0;

        void Add(string label, string value, int points)
        {
            penalty += points;
            factors.Add(SummaryRow.Create(SummarySection.Reliability, c, label, value, points > 0));
        }

        var duration = s.DurationSeconds;
        Add(Loc.T("Durata utile", c), string.Format(c, "{0:F0} s", duration), duration < 5 ? 20 : 0);

        // Sensori di movimento: attesi 100 campioni al secondo.
        var motion = Rate(s, SensorKind.Motion);
        Add(Loc.T("Sensori di movimento", c), motion is { } m ? string.Format(c, Loc.T("{0:F0} campioni/s su 100", c), m) : Loc.T("assenti", c),
            motion switch { null => 30, < 50 => 30, < 90 => 15, _ => 0 });

        // Visione: fotogrammi analizzati da MediaPipe (o volto ARKit) al secondo.
        var (visionKind, visionName) = s.Mode switch
        {
            CaptureMode.HandsLidar => (SensorKind.HandLandmarks, Loc.T("Riconoscimento delle mani", c)),
            CaptureMode.FaceTrueDepth => (SensorKind.FaceBlendshapes, Loc.T("Tracciamento del volto", c)),
            _ => (SensorKind.JointAngles, Loc.T("Riconoscimento del corpo", c)),
        };
        var vision = Rate(s, visionKind);
        Add(visionName, vision is { } v ? string.Format(c, Loc.T("{0:F0} fotogrammi/s", c), v) : Loc.T("nessun fotogramma", c),
            vision switch { null => 40, < 8 => 30, < 15 => 15, _ => 0 });

        var total = s.SampleCounts.Values.Sum();
        var lost = total + s.DroppedSamples > 0 ? s.DroppedSamples / (double)(total + s.DroppedSamples) : 0;
        Add(Loc.T("Campioni persi", c), string.Format(c, "{0:P1}", lost), lost switch { > 0.05 => 30, > 0.01 => 15, > 0 => 5, _ => 0 });

        if (s.Mode == CaptureMode.BodyLidar && s.MedianSubjectDistanceMeters is { } distance)
        {
            Add(Loc.T("Distanza del soggetto", c), string.Format(c, Loc.T("{0:F1} m (ideale 1–4,5)", c), distance), distance is < 1 or > 4.5 ? 10 : 0);
        }

        // La copertura LiDAR conta solo se il LiDAR era acceso: senza LiDAR è assente (zero nei riepiloghi meno recenti).
        if (s.LidarCoverage is { } coverage && coverage > 0)
        {
            Add(Loc.T("Copertura LiDAR", c), string.Format(c, "{0:P0}", coverage), coverage < 0.6 ? 10 : 0);
        }

        if (s.Audio is { } audio)
        {
            Add(Loc.T("Saturazione audio", c), string.Format(c, "{0:P2}", audio.ClippingRatio), audio.ClippingRatio > 0.001 ? 5 : 0);
        }

        if (s.Test is { Estimated: true })
        {
            Add(Loc.T("Punteggio del test", c), Loc.T("stima automatica da verificare", c), 5);
        }

        var score = Math.Clamp(100 - penalty, 0, 100);
        var level = score >= 85 ? ReliabilityLevel.High : score >= 65 ? ReliabilityLevel.Medium : ReliabilityLevel.Low;
        var label = level switch { ReliabilityLevel.High => Loc.T("alta", c), ReliabilityLevel.Medium => Loc.T("media", c), _ => Loc.T("bassa", c) };
        factors.Insert(0, SummaryRow.Create(SummarySection.Reliability, c, Loc.T("Punteggio", c), string.Format(c, "{0}/100 · {1}", score, label), level != ReliabilityLevel.High));
        return new ReliabilityReport(score, level, factors);
    }

    private static double? Rate(SessionSummary s, SensorKind kind) =>
        s.DurationSeconds > 0 && s.SampleCounts.TryGetValue(kind, out var count) && count > 0 ? count / s.DurationSeconds : null;
}
