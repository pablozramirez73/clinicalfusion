using ClinicalSensorFusion.Core.Localization;
using System.Globalization;
using System.Text.Json.Serialization;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>
/// Sezione del riepilogo. Chi raggruppa o filtra le righe usa questa, mai il titolo: il titolo è tradotto e
/// cambia con la lingua.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SummarySection>))]
public enum SummarySection
{
    General,
    JointRange,
    Tremor,
    PedometerGait,
    PoseGait,
    Audio,
    EnvironmentSensors,
    DataQuality,
    Reliability,
    Payload,
}

/// <summary>Riga del riepilogo leggibile: <paramref name="Kind"/> la identifica, <paramref name="Section"/> è il titolo da mostrare.</summary>
public sealed record SummaryRow(SummarySection Kind, string Section, string Label, string Value, bool Warning = false)
{
    /// <summary>Riga con il titolo della sezione nella lingua di <paramref name="culture"/>.</summary>
    public static SummaryRow Create(SummarySection kind, CultureInfo culture, string label, string value, bool warning = false) =>
        new(kind, SummaryReport.SectionTitle(kind, culture), label, value, warning);
}

/// <summary>Un canale di un sensore ambientale: valore principale e, se è variato, intervallo e media.</summary>
public sealed record SensorTile(string Name, string Value, string Detail, bool Warning = false)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>Un sensore ambientale con i suoi canali.</summary>
public sealed record SensorCard(string Sensor, IReadOnlyList<SensorTile> Tiles);

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
            Row(SummarySection.General, c, Loc.T("Durata", c), TimeSpan.FromSeconds(s.DurationSeconds).ToString(@"mm\:ss", c)),
        };

        if (s.MedianSubjectDistanceMeters is { } distance)
        {
            rows.Add(Row(SummarySection.General, c, Loc.T("Distanza mediana soggetto", c), string.Format(c, "{0:F2} m", distance), distance > 4.5));
        }

        if (s.LidarCoverage is { } coverage)
        {
            rows.Add(Row(SummarySection.General, c, Loc.T("Copertura LiDAR", c), string.Format(c, "{0:P0}", coverage), coverage < 0.6));
        }

        foreach (var (joint, range) in s.JointRanges.OrderBy(j => j.Key))
        {
            rows.Add(Row(SummarySection.JointRange, c, JointName(joint),
                string.Format(c, "{0:F0}°–{1:F0}° (ROM {2:F0}°)", range.Min, range.Max, range.RangeOfMotion), range.Samples < 30));
        }

        if (s.Tremor is { } tremor)
        {
            rows.Add(Row(SummarySection.Tremor, c, Loc.T("Frequenza dominante", c),
                tremor.IsSignificant ? string.Format(c, "{0:F1} Hz", tremor.DominantFrequencyHz) : Loc.T("nessun tremore significativo", c)));
            rows.Add(Row(SummarySection.Tremor, c, Loc.T("Potenza in banda 3–12 Hz", c), string.Format(c, "{0:P0}", tremor.BandPowerRatio)));
            rows.Add(Row(SummarySection.Tremor, c, Loc.T("Ampiezza RMS", c), string.Format(c, "{0:F3} m/s²", tremor.BandRmsMs2)));
        }

        if (s.Gait is { } gait)
        {
            rows.Add(Row(SummarySection.PedometerGait, c, Loc.T("Passi", c), gait.Steps.ToString(c)));
            rows.Add(Row(SummarySection.PedometerGait, c, Loc.T("Distanza", c), double.IsFinite(gait.DistanceMeters) ? string.Format(c, "{0:F1} m", gait.DistanceMeters) : "—"));
            if (gait.CadenceStepsPerMinute is { } cadence)
            {
                rows.Add(Row(SummarySection.PedometerGait, c, Loc.T("Cadenza", c), string.Format(c, Loc.T("{0:F0} passi/min", c), cadence)));
            }

            if (gait.FloorsAscended + gait.FloorsDescended > 0)
            {
                rows.Add(Row(SummarySection.PedometerGait, c, Loc.T("Piani saliti / scesi", c), string.Format(c, "{0} / {1}", gait.FloorsAscended, gait.FloorsDescended)));
            }
        }

        if (s.PoseGait is { } poseGait)
        {
            rows.Add(Row(SummarySection.PoseGait, c, Loc.T("Passi riconosciuti", c), poseGait.Steps.ToString(c)));
            rows.Add(Row(SummarySection.PoseGait, c, Loc.T("Cadenza", c), string.Format(c, Loc.T("{0:F0} passi/min", c), poseGait.CadenceStepsPerMinute)));
            rows.Add(Row(SummarySection.PoseGait, c, Loc.T("Variabilità della durata del passo", c), string.Format(c, "{0:P0}", poseGait.StepTimeCv), poseGait.StepTimeCv > 0.1));
        }

        if (s.Audio is { } audio)
        {
            rows.Add(Row(SummarySection.Audio, c, Loc.T("Durata registrazione", c), string.Format(c, "{0:F1} s", audio.DurationSeconds)));
            rows.Add(Row(SummarySection.Audio, c, Loc.T("Livello RMS", c), string.Format(c, "{0:F1} dBFS", audio.RmsDbfs)));
            rows.Add(Row(SummarySection.Audio, c, Loc.T("Picco", c), string.Format(c, "{0:F1} dBFS", audio.PeakDbfs)));
            rows.Add(Row(SummarySection.Audio, c, Loc.T("Tempo con segnale attivo", c), string.Format(c, "{0:P0}", audio.ActiveRatio)));
            rows.Add(Row(SummarySection.Audio, c, Loc.T("Saturazione", c), string.Format(c, "{0:P2}", audio.ClippingRatio), audio.ClippingRatio > 0.001));
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
                rows.Add(Row(SummarySection.EnvironmentSensors, c, $"{StreamName(kind)} · {channel.Name}", Describe(channel, c)));
            }
        }

        foreach (var (kind, count) in s.SampleCounts.OrderBy(k => k.Key))
        {
            rows.Add(Row(SummarySection.DataQuality, c, StreamName(kind), string.Format(c, Loc.T("{0:N0} campioni", c), count)));
        }

        rows.Add(Row(SummarySection.DataQuality, c, Loc.T("Campioni persi", c), s.DroppedSamples.ToString("N0", c), s.DroppedSamples > 0));
        return rows;
    }

    /// <summary>Titolo della sezione nella lingua di <paramref name="culture"/>.</summary>
    public static string SectionTitle(SummarySection section, CultureInfo culture) => Loc.T(section switch
    {
        SummarySection.General => "Generale",
        SummarySection.JointRange => "Escursione articolare (ROM)",
        SummarySection.Tremor => "Tremore (accelerometro)",
        SummarySection.PedometerGait => "Cammino (pedometro)",
        SummarySection.PoseGait => "Cammino (posa)",
        SummarySection.Audio => "Voce e suoni (microfono)",
        SummarySection.EnvironmentSensors => "Sensori ambientali",
        SummarySection.DataQuality => "Qualità del dato",
        SummarySection.Reliability => "Affidabilità della misura",
        SummarySection.Payload => "Payload JSON",
        _ => section.ToString(),
    }, culture);

    private static SummaryRow Row(SummarySection section, CultureInfo c, string label, string value, bool warning = false) =>
        SummaryRow.Create(section, c, label, value, warning);

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

    /// <summary>
    /// Sensori ambientali come schede: una per sensore, con un riquadro per canale. Il valore principale è
    /// l'ultimo letto; intervallo e media vanno nel dettaglio solo se il canale è cambiato.
    /// </summary>
    public static IReadOnlyList<SensorCard> Environment(SessionSummary s, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var cards = new List<SensorCard>();
        foreach (var (kind, stream) in (s.Sensors ?? []).OrderBy(k => k.Key))
        {
            var tiles = kind == SensorKind.Location
                ? LocationTiles(stream, c)
                : [.. stream.Channels.Select(ch => Tile(Loc.T(ch.Name, c), ch, c))];
            if (tiles.Count > 0)
            {
                cards.Add(new SensorCard(StreamName(kind), tiles));
            }
        }

        return cards;
    }

    private static SensorTile Tile(string name, ChannelStats ch, CultureInfo c)
    {
        var unit = ch.Unit.Length == 0 ? string.Empty : char.IsDigit(ch.Unit[0]) ? " " + string.Format(c, Loc.T("(scala {0})", c), ch.Unit) : " " + Loc.T(ch.Unit, c);
        var (min, max) = (ch.Min.ToString("0.##", c), ch.Max.ToString("0.##", c));
        var detail = min == max ? string.Empty : string.Format(c, Loc.T("{0}–{1} · media {2:0.##}", c), min, max, ch.Mean);
        return new SensorTile(name, string.Format(c, "{0:0.##}{1}", ch.Last, unit), detail);
    }

    private static List<SensorTile> LocationTiles(SensorStreamSummary stream, CultureInfo c)
    {
        var tiles = new List<SensorTile>();
        var byName = stream.Channels.ToDictionary(ch => ch.Name);
        if (byName.TryGetValue("Latitudine", out var lat) && byName.TryGetValue("Longitudine", out var lon))
        {
            tiles.Add(new(Loc.T("Posizione", c), string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}", lat.Last, lon.Last),
                string.Format(c, Loc.T("{0:N0} rilevamenti", c), lat.Samples)));
        }

        if (byName.TryGetValue("Accuratezza orizzontale", out var accuracy))
        {
            tiles.Add(new(Loc.T("Accuratezza", c), string.Format(c, "±{0:F0} m", accuracy.Last),
                string.Format(c, Loc.T("migliore ±{0:F0} m", c), accuracy.Min), accuracy.Min > 50));
        }

        tiles.AddRange(stream.Channels.Where(ch => ch.Name is "Altitudine" or "Velocità").Select(ch => Tile(Loc.T(ch.Name, c), ch, c)));
        return tiles;
    }

    /// <summary>La posizione si legge come coppia di coordinate, non come cinque statistiche separate.</summary>
    private static void AddLocation(List<SummaryRow> rows, SensorStreamSummary stream, CultureInfo c)
    {
        var name = StreamName(SensorKind.Location);
        var byName = stream.Channels.ToDictionary(ch => ch.Name);
        if (byName.TryGetValue("Latitudine", out var lat) && byName.TryGetValue("Longitudine", out var lon))
        {
            rows.Add(Row(SummarySection.EnvironmentSensors, c, $"{name} · Posizione", string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}", lat.Last, lon.Last)));
            rows.Add(Row(SummarySection.EnvironmentSensors, c, $"{name} · Rilevamenti", lat.Samples.ToString("N0", c)));
        }

        if (byName.TryGetValue("Accuratezza orizzontale", out var accuracy))
        {
            rows.Add(Row(SummarySection.EnvironmentSensors, c, $"{name} · Accuratezza", string.Format(c, Loc.T("±{0:F0} m (migliore ±{1:F0} m)", c), accuracy.Last, accuracy.Min), accuracy.Min > 50));
        }

        foreach (var channel in stream.Channels.Where(ch => ch.Name is "Altitudine" or "Velocità"))
        {
            rows.Add(Row(SummarySection.EnvironmentSensors, c, $"{name} · {channel.Name}", Describe(channel, c)));
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
