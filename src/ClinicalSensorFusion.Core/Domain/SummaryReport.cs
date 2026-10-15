using ClinicalSensorFusion.Core.Localization;
using System.Globalization;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Recording;
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
            new(Loc.T("Generale", c), Loc.T("Durata", c), TimeSpan.FromSeconds(s.DurationSeconds).ToString(@"mm\:ss", c)),
        };

        if (s.MedianSubjectDistanceMeters is { } distance)
        {
            rows.Add(new(Loc.T("Generale", c), Loc.T("Distanza mediana soggetto", c), string.Format(c, "{0:F2} m", distance), distance > 4.5));
        }

        if (s.LidarCoverage is { } coverage)
        {
            rows.Add(new(Loc.T("Generale", c), Loc.T("Copertura LiDAR", c), string.Format(c, "{0:P0}", coverage), coverage < 0.6));
        }

        foreach (var (joint, range) in s.JointRanges.OrderBy(j => j.Key))
        {
            rows.Add(new(Loc.T("Escursione articolare (ROM)", c), JointName(joint),
                string.Format(c, "{0:F0}°–{1:F0}° (ROM {2:F0}°)", range.Min, range.Max, range.RangeOfMotion), range.Samples < 30));
        }

        if (s.Tremor is { } tremor)
        {
            rows.Add(new(Loc.T("Tremore (accelerometro)", c), Loc.T("Frequenza dominante", c),
                tremor.IsSignificant ? string.Format(c, "{0:F1} Hz", tremor.DominantFrequencyHz) : Loc.T("nessun tremore significativo", c)));
            rows.Add(new(Loc.T("Tremore (accelerometro)", c), Loc.T("Potenza in banda 3–12 Hz", c), string.Format(c, "{0:P0}", tremor.BandPowerRatio)));
            rows.Add(new(Loc.T("Tremore (accelerometro)", c), Loc.T("Ampiezza RMS", c), string.Format(c, "{0:F3} m/s²", tremor.BandRmsMs2)));
        }

        if (s.Gait is { } gait)
        {
            rows.Add(new(Loc.T("Cammino (pedometro)", c), Loc.T("Passi", c), gait.Steps.ToString(c)));
            rows.Add(new(Loc.T("Cammino (pedometro)", c), Loc.T("Distanza", c), double.IsFinite(gait.DistanceMeters) ? string.Format(c, "{0:F1} m", gait.DistanceMeters) : "—"));
            if (gait.CadenceStepsPerMinute is { } cadence)
            {
                rows.Add(new(Loc.T("Cammino (pedometro)", c), Loc.T("Cadenza", c), string.Format(c, Loc.T("{0:F0} passi/min", c), cadence)));
            }

            if (gait.FloorsAscended + gait.FloorsDescended > 0)
            {
                rows.Add(new(Loc.T("Cammino (pedometro)", c), Loc.T("Piani saliti / scesi", c), string.Format(c, "{0} / {1}", gait.FloorsAscended, gait.FloorsDescended)));
            }
        }

        if (s.PoseGait is { } poseGait)
        {
            rows.Add(new(Loc.T("Cammino (posa)", c), Loc.T("Passi riconosciuti", c), poseGait.Steps.ToString(c)));
            rows.Add(new(Loc.T("Cammino (posa)", c), Loc.T("Cadenza", c), string.Format(c, Loc.T("{0:F0} passi/min", c), poseGait.CadenceStepsPerMinute)));
            rows.Add(new(Loc.T("Cammino (posa)", c), Loc.T("Variabilità della durata del passo", c), string.Format(c, "{0:P0}", poseGait.StepTimeCv), poseGait.StepTimeCv > 0.1));
        }

        if (s.Audio is { } audio)
        {
            rows.Add(new(Loc.T("Voce e suoni (microfono)", c), Loc.T("Durata registrazione", c), string.Format(c, "{0:F1} s", audio.DurationSeconds)));
            rows.Add(new(Loc.T("Voce e suoni (microfono)", c), Loc.T("Livello RMS", c), string.Format(c, "{0:F1} dBFS", audio.RmsDbfs)));
            rows.Add(new(Loc.T("Voce e suoni (microfono)", c), Loc.T("Picco", c), string.Format(c, "{0:F1} dBFS", audio.PeakDbfs)));
            rows.Add(new(Loc.T("Voce e suoni (microfono)", c), Loc.T("Tempo con segnale attivo", c), string.Format(c, "{0:P0}", audio.ActiveRatio)));
            rows.Add(new(Loc.T("Voce e suoni (microfono)", c), Loc.T("Saturazione", c), string.Format(c, "{0:P2}", audio.ClippingRatio), audio.ClippingRatio > 0.001));
        }

        foreach (var (kind, stream) in (s.Sensors ?? []).OrderBy(k => k.Key))
        {
            if (kind == SensorKind.Location)
            {
                AddLocation(rows, stream, c);
                continue;
            }

            foreach (var channel in stream.Channels)
            {
                rows.Add(new(SensorsSection, $"{StreamName(kind)} · {channel.Name}", Describe(channel, c)));
            }
        }

        foreach (var (kind, count) in s.SampleCounts.OrderBy(k => k.Key))
        {
            rows.Add(new(Loc.T("Qualità del dato", c), StreamName(kind), string.Format(c, Loc.T("{0:N0} campioni", c), count)));
        }

        rows.Add(new(Loc.T("Qualità del dato", c), Loc.T("Campioni persi", c), s.DroppedSamples.ToString("N0", c), s.DroppedSamples > 0));
        return rows;
    }

    public const string SensorsSection = "Sensori ambientali";

    /// <summary>Un solo valore se il canale non è mai cambiato, altrimenti intervallo e media.</summary>
    private static string Describe(ChannelStats ch, CultureInfo c)
    {
        // Le scale (0–1, 0–3) non sono unità: vanno tra parentesi per non leggersi come parte del valore.
        var unit = ch.Unit.Length == 0 ? string.Empty : char.IsDigit(ch.Unit[0]) ? $" (scala {ch.Unit})" : " " + ch.Unit;
        var (min, max) = (ch.Min.ToString("0.##", c), ch.Max.ToString("0.##", c));
        return min == max
            ? string.Format(c, "{0:0.##}{1}", ch.Last, unit)
            : string.Format(c, Loc.T("{0}–{1}{3} (media {2:0.##}, ultimo {4:0.##})", c), min, max, ch.Mean, unit, ch.Last);
    }

    /// <summary>La posizione si legge come coppia di coordinate, non come cinque statistiche separate.</summary>
    private static void AddLocation(List<SummaryRow> rows, SensorStreamSummary stream, CultureInfo c)
    {
        var name = StreamName(SensorKind.Location);
        var byName = stream.Channels.ToDictionary(ch => ch.Name);
        if (byName.TryGetValue("Latitudine", out var lat) && byName.TryGetValue("Longitudine", out var lon))
        {
            rows.Add(new(SensorsSection, $"{name} · Posizione", string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}", lat.Last, lon.Last)));
            rows.Add(new(SensorsSection, $"{name} · Rilevamenti", lat.Samples.ToString("N0", c)));
        }

        if (byName.TryGetValue("Accuratezza orizzontale", out var accuracy))
        {
            rows.Add(new(SensorsSection, $"{name} · Accuratezza", string.Format(c, Loc.T("±{0:F0} m (migliore ±{1:F0} m)", c), accuracy.Last, accuracy.Min), accuracy.Min > 50));
        }

        foreach (var channel in stream.Channels.Where(ch => ch.Name is "Altitudine" or "Velocità"))
        {
            rows.Add(new(SensorsSection, $"{name} · {channel.Name}", Describe(channel, c)));
        }
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
        SensorKind.HeartRate => "Cardiofrequenzimetro (Bluetooth)",
        SensorKind.PulseOximeter => "Saturimetro (Bluetooth)",
        SensorKind.BodyTemperature => "Termometro (Bluetooth)",
        _ => kind.ToString(),
    };
}
