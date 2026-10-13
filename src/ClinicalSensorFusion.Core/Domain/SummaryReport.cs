using System.Globalization;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Localization;
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
        [Joint.LeftShoulderElevation] = Loc.N("Elevazione spalla sx"),
        [Joint.RightShoulderElevation] = Loc.N("Elevazione spalla dx"),
        [Joint.LeftElbowFlexion] = Loc.N("Flessione gomito sx"),
        [Joint.RightElbowFlexion] = Loc.N("Flessione gomito dx"),
        [Joint.LeftHipFlexion] = Loc.N("Flessione anca sx"),
        [Joint.RightHipFlexion] = Loc.N("Flessione anca dx"),
        [Joint.LeftKneeFlexion] = Loc.N("Flessione ginocchio sx"),
        [Joint.RightKneeFlexion] = Loc.N("Flessione ginocchio dx"),
        [Joint.LeftAnkle] = Loc.N("Angolo caviglia sx"),
        [Joint.RightAnkle] = Loc.N("Angolo caviglia dx"),
    };

    /// <summary>Nome dell'articolazione nella lingua dell'interfaccia (sul server, che non la cambia mai, in italiano).</summary>
    public static string JointName(Joint joint) => JointNames.TryGetValue(joint, out var name) ? Loc.T(name) : joint.ToString();

    public static IReadOnlyList<SummaryRow> Build(SessionSummary s, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var rows = new List<SummaryRow>
        {
            new(Loc.T("Generale"), Loc.T("Durata"), TimeSpan.FromSeconds(s.DurationSeconds).ToString(@"mm\:ss", c)),
        };

        if (s.MedianSubjectDistanceMeters is { } distance)
        {
            rows.Add(new(Loc.T("Generale"), Loc.T("Distanza mediana soggetto"), string.Format(c, "{0:F2} m", distance), distance > 4.5));
        }

        if (s.LidarCoverage is { } coverage)
        {
            rows.Add(new(Loc.T("Generale"), Loc.T("Copertura LiDAR"), string.Format(c, "{0:P0}", coverage), coverage < 0.6));
        }

        foreach (var (joint, range) in s.JointRanges.OrderBy(j => j.Key))
        {
            rows.Add(new(Loc.T("Escursione articolare (ROM)"), JointName(joint),
                string.Format(c, "{0:F0}°–{1:F0}° (ROM {2:F0}°)", range.Min, range.Max, range.RangeOfMotion), range.Samples < 30));
        }

        if (s.Tremor is { } tremor)
        {
            rows.Add(new(Loc.T("Tremore (accelerometro)"), Loc.T("Frequenza dominante"),
                tremor.IsSignificant ? string.Format(c, "{0:F1} Hz", tremor.DominantFrequencyHz) : Loc.T("nessun tremore significativo")));
            rows.Add(new(Loc.T("Tremore (accelerometro)"), Loc.T("Potenza in banda 3–12 Hz"), string.Format(c, "{0:P0}", tremor.BandPowerRatio)));
            rows.Add(new(Loc.T("Tremore (accelerometro)"), Loc.T("Ampiezza RMS"), string.Format(c, "{0:F3} m/s²", tremor.BandRmsMs2)));
        }

        if (s.Gait is { } gait)
        {
            rows.Add(new(Loc.T("Cammino (pedometro)"), Loc.T("Passi"), gait.Steps.ToString(c)));
            rows.Add(new(Loc.T("Cammino (pedometro)"), Loc.T("Distanza"), double.IsFinite(gait.DistanceMeters) ? string.Format(c, "{0:F1} m", gait.DistanceMeters) : "—"));
            if (gait.CadenceStepsPerMinute is { } cadence)
            {
                rows.Add(new(Loc.T("Cammino (pedometro)"), Loc.T("Cadenza"), string.Format(c, Loc.T("{0:F0} passi/min"), cadence)));
            }

            if (gait.FloorsAscended + gait.FloorsDescended > 0)
            {
                rows.Add(new(Loc.T("Cammino (pedometro)"), Loc.T("Piani saliti / scesi"), string.Format(c, "{0} / {1}", gait.FloorsAscended, gait.FloorsDescended)));
            }
        }

        if (s.Audio is { } audio)
        {
            rows.Add(new(Loc.T("Voce e suoni (microfono)"), Loc.T("Durata registrazione"), string.Format(c, "{0:F1} s", audio.DurationSeconds)));
            rows.Add(new(Loc.T("Voce e suoni (microfono)"), Loc.T("Livello RMS"), string.Format(c, "{0:F1} dBFS", audio.RmsDbfs)));
            rows.Add(new(Loc.T("Voce e suoni (microfono)"), Loc.T("Picco"), string.Format(c, "{0:F1} dBFS", audio.PeakDbfs)));
            rows.Add(new(Loc.T("Voce e suoni (microfono)"), Loc.T("Tempo con segnale attivo"), string.Format(c, "{0:P0}", audio.ActiveRatio)));
            rows.Add(new(Loc.T("Voce e suoni (microfono)"), Loc.T("Saturazione"), string.Format(c, "{0:P2}", audio.ClippingRatio), audio.ClippingRatio > 0.001));
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
                rows.Add(new(Loc.T(SensorsSection), $"{StreamName(kind)} · {Loc.T(channel.Name)}", Describe(channel, c)));
            }
        }

        foreach (var (kind, count) in s.SampleCounts.OrderBy(k => k.Key))
        {
            rows.Add(new(Loc.T("Qualità del dato"), StreamName(kind), string.Format(c, Loc.T("{0:N0} campioni"), count)));
        }

        rows.Add(new(Loc.T("Qualità del dato"), Loc.T("Campioni persi"), s.DroppedSamples.ToString("N0", c), s.DroppedSamples > 0));
        return rows;
    }

    /// <summary>Sezione dei sensori ambientali in italiano, cioè la chiave di traduzione: le righe la portano tradotta.</summary>
    public const string SensorsSection = "Sensori ambientali";

    /// <summary>Un solo valore se il canale non è mai cambiato, altrimenti intervallo e media.</summary>
    private static string Describe(ChannelStats ch, CultureInfo c)
    {
        // Le scale (0–1, 0–3) non sono unità: vanno tra parentesi per non leggersi come parte del valore.
        var unit = ch.Unit.Length == 0 ? string.Empty : char.IsDigit(ch.Unit[0]) ? " " + Loc.F("(scala {0})", ch.Unit) : " " + Loc.T(ch.Unit);
        var (min, max) = (ch.Min.ToString("0.##", c), ch.Max.ToString("0.##", c));
        return min == max
            ? string.Format(c, "{0:0.##}{1}", ch.Last, unit)
            : string.Format(c, Loc.T("{0}–{1}{3} (media {2:0.##}, ultimo {4:0.##})"), min, max, ch.Mean, unit, ch.Last);
    }

    /// <summary>La posizione si legge come coppia di coordinate, non come cinque statistiche separate.</summary>
    private static void AddLocation(List<SummaryRow> rows, SensorStreamSummary stream, CultureInfo c)
    {
        var name = StreamName(SensorKind.Location);
        var byName = stream.Channels.ToDictionary(ch => ch.Name);
        if (byName.TryGetValue("Latitudine", out var lat) && byName.TryGetValue("Longitudine", out var lon))
        {
            rows.Add(new(Loc.T(SensorsSection), $"{name} · {Loc.T("Posizione")}", string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}", lat.Last, lon.Last)));
            rows.Add(new(Loc.T(SensorsSection), $"{name} · {Loc.T("Rilevamenti")}", lat.Samples.ToString("N0", c)));
        }

        if (byName.TryGetValue("Accuratezza orizzontale", out var accuracy))
        {
            rows.Add(new(Loc.T(SensorsSection), $"{name} · {Loc.T("Accuratezza")}", string.Format(c, Loc.T("±{0:F0} m (migliore ±{1:F0} m)"), accuracy.Last, accuracy.Min), accuracy.Min > 50));
        }

        foreach (var channel in stream.Channels.Where(ch => ch.Name is "Altitudine" or "Velocità"))
        {
            rows.Add(new(Loc.T(SensorsSection), $"{name} · {Loc.T(channel.Name)}", Describe(channel, c)));
        }
    }

    public static string StreamName(SensorKind kind) => kind switch
    {
        SensorKind.Motion => "IMU (100 Hz)",
        SensorKind.Barometer => Loc.T("Barometro"),
        SensorKind.Pedometer => Loc.T("Pedometro"),
        SensorKind.Location => "GPS",
        SensorKind.Proximity => Loc.T("Prossimità"),
        SensorKind.AmbientLight => Loc.T("Luce ambientale"),
        SensorKind.DeviceState => Loc.T("Stato dispositivo"),
        SensorKind.LidarDepth => "LiDAR",
        SensorKind.AudioLevel => Loc.T("Livelli audio"),
        SensorKind.PoseLandmarks2D => "Pose 2D (MediaPipe)",
        SensorKind.PoseLandmarks3D => Loc.T("Pose 3D (fusione)"),
        SensorKind.JointAngles => Loc.T("Angoli articolari"),
        SensorKind.HandLandmarks => Loc.T("Mani (MediaPipe)"),
        SensorKind.FaceBlendshapes => Loc.T("Blendshape volto (TrueDepth)"),
        SensorKind.HeartRate => Loc.T("Cardiofrequenzimetro (Bluetooth)"),
        SensorKind.PulseOximeter => Loc.T("Saturimetro (Bluetooth)"),
        SensorKind.BodyTemperature => Loc.T("Termometro (Bluetooth)"),
        _ => kind.ToString(),
    };
}
