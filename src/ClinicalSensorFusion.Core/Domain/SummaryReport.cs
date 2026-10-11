using System.Globalization;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>Riga del riepilogo leggibile, raggruppata per sezione.</summary>
public sealed record SummaryRow(string Section, string Label, string Value, bool Warning = false);

/// <summary>
/// Trasforma il riepilogo in righe leggibili per la schermata di dettaglio. Le soglie di "attenzione"
/// sono indicatori di qualità del dato, non giudizi clinici.
/// </summary>
public static class SummaryReport
{
    private static readonly Dictionary<Joint, string> JointNames = new()
    {
        [Joint.LeftShoulderElevation] = "Elevazione spalla sx",
        [Joint.RightShoulderElevation] = "Elevazione spalla dx",
        [Joint.LeftElbowFlexion] = "Flessione gomito sx",
        [Joint.RightElbowFlexion] = "Flessione gomito dx",
        [Joint.LeftHipFlexion] = "Flessione anca sx",
        [Joint.RightHipFlexion] = "Flessione anca dx",
        [Joint.LeftKneeFlexion] = "Flessione ginocchio sx",
        [Joint.RightKneeFlexion] = "Flessione ginocchio dx",
        [Joint.LeftAnkle] = "Angolo caviglia sx",
        [Joint.RightAnkle] = "Angolo caviglia dx",
    };

    public static string JointName(Joint joint) => JointNames.GetValueOrDefault(joint, joint.ToString());

    public static IReadOnlyList<SummaryRow> Build(SessionSummary s, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var rows = new List<SummaryRow>
        {
            new("Generale", "Durata", TimeSpan.FromSeconds(s.DurationSeconds).ToString(@"mm\:ss", c)),
        };

        if (s.MedianSubjectDistanceMeters is { } distance)
        {
            rows.Add(new("Generale", "Distanza mediana soggetto", string.Format(c, "{0:F2} m", distance), distance > 4.5));
        }

        if (s.LidarCoverage is { } coverage)
        {
            rows.Add(new("Generale", "Copertura LiDAR", string.Format(c, "{0:P0}", coverage), coverage < 0.6));
        }

        foreach (var (joint, range) in s.JointRanges.OrderBy(j => j.Key))
        {
            rows.Add(new("Escursione articolare (ROM)", JointName(joint),
                string.Format(c, "{0:F0}°–{1:F0}° (ROM {2:F0}°)", range.Min, range.Max, range.RangeOfMotion), range.Samples < 30));
        }

        if (s.Tremor is { } tremor)
        {
            rows.Add(new("Tremore (accelerometro)", "Frequenza dominante",
                double.IsFinite(tremor.DominantFrequencyHz) ? string.Format(c, "{0:F1} Hz", tremor.DominantFrequencyHz) : "—"));
            rows.Add(new("Tremore (accelerometro)", "Potenza in banda 3–12 Hz", string.Format(c, "{0:P0}", tremor.BandPowerRatio)));
            rows.Add(new("Tremore (accelerometro)", "Ampiezza RMS", string.Format(c, "{0:F3} m/s²", tremor.BandRmsMs2)));
        }

        if (s.Gait is { } gait)
        {
            rows.Add(new("Cammino (pedometro)", "Passi", gait.Steps.ToString(c)));
            rows.Add(new("Cammino (pedometro)", "Distanza", double.IsFinite(gait.DistanceMeters) ? string.Format(c, "{0:F1} m", gait.DistanceMeters) : "—"));
            if (gait.CadenceStepsPerMinute is { } cadence)
            {
                rows.Add(new("Cammino (pedometro)", "Cadenza", string.Format(c, "{0:F0} passi/min", cadence)));
            }

            if (gait.FloorsAscended + gait.FloorsDescended > 0)
            {
                rows.Add(new("Cammino (pedometro)", "Piani saliti / scesi", string.Format(c, "{0} / {1}", gait.FloorsAscended, gait.FloorsDescended)));
            }
        }

        if (s.Audio is { } audio)
        {
            rows.Add(new("Voce e suoni (microfono)", "Durata registrazione", string.Format(c, "{0:F1} s", audio.DurationSeconds)));
            rows.Add(new("Voce e suoni (microfono)", "Livello RMS", string.Format(c, "{0:F1} dBFS", audio.RmsDbfs)));
            rows.Add(new("Voce e suoni (microfono)", "Picco", string.Format(c, "{0:F1} dBFS", audio.PeakDbfs)));
            rows.Add(new("Voce e suoni (microfono)", "Tempo con segnale attivo", string.Format(c, "{0:P0}", audio.ActiveRatio)));
            rows.Add(new("Voce e suoni (microfono)", "Saturazione", string.Format(c, "{0:P2}", audio.ClippingRatio), audio.ClippingRatio > 0.001));
        }

        foreach (var (kind, count) in s.SampleCounts.OrderBy(k => k.Key))
        {
            rows.Add(new("Qualità del dato", StreamName(kind), string.Format(c, "{0:N0} campioni", count)));
        }

        rows.Add(new("Qualità del dato", "Campioni persi", s.DroppedSamples.ToString("N0", c), s.DroppedSamples > 0));
        return rows;
    }

    public static string StreamName(SensorKind kind) => kind switch
    {
        SensorKind.Motion => "IMU (100 Hz)",
        SensorKind.Barometer => "Barometro",
        SensorKind.Pedometer => "Pedometro",
        SensorKind.Location => "GPS",
        SensorKind.Proximity => "Prossimità",
        SensorKind.AmbientLight => "Luce ambientale",
        SensorKind.DeviceState => "Stato dispositivo",
        SensorKind.LidarDepth => "LiDAR",
        SensorKind.AudioLevel => "Livelli audio",
        SensorKind.PoseLandmarks2D => "Pose 2D (MediaPipe)",
        SensorKind.PoseLandmarks3D => "Pose 3D (fusione)",
        SensorKind.JointAngles => "Angoli articolari",
        SensorKind.HandLandmarks => "Mani (MediaPipe)",
        SensorKind.FaceBlendshapes => "Blendshape volto (TrueDepth)",
        _ => kind.ToString(),
    };
}
