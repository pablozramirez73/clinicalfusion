using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Recording;

/// <summary>Statistiche di un canale di un sensore su tutta la rilevazione (i campioni NaN non contano).</summary>
/// <param name="Name">Nome del canale, es. «Pressione».</param>
/// <param name="Unit">Unità di misura; vuota se adimensionale.</param>
/// <param name="First">Primo valore valido.</param>
/// <param name="Last">Ultimo valore valido (per i contatori cumulativi, come i passi, è il totale).</param>
public sealed record ChannelStats(string Name, string Unit, double Min, double Max, double Mean, double First, double Last, long Samples);

/// <summary>Un sensore con i suoi canali.</summary>
public sealed record SensorStreamSummary(IReadOnlyList<ChannelStats> Channels);

/// <summary>
/// Riepilogo per canale dei sensori "ambientali" (barometro, GPS, luce, prossimità, stato del dispositivo,
/// LiDAR, livelli audio, pedometro) e dei sensori Bluetooth collegati (frequenza cardiaca, saturimetro, termometro): ciò che ogni sensore acceso ha segnalato, in forma compatta. I flussi
/// grezzi restano nei blocchi; IMU e visione hanno indicatori clinici propri e non compaiono qui.
/// </summary>
public sealed class SensorStreamStats
{
    /// <summary>Canali di ogni flusso, nell'ordine di <see cref="SensorReading.Values"/> (vedi <see cref="SensorKind"/>).</summary>
    private static readonly Dictionary<SensorKind, (string Name, string Unit)[]> Layouts = new()
    {
        [SensorKind.Barometer] = [(Loc.N("Altitudine relativa"), "m"), (Loc.N("Pressione"), "kPa")],
        [SensorKind.Pedometer] = [(Loc.N("Passi"), ""), (Loc.N("Distanza"), "m"), (Loc.N("Cadenza"), Loc.N("passi/s")), (Loc.N("Piani saliti"), ""), (Loc.N("Piani scesi"), "")],
        [SensorKind.Location] = [(Loc.N("Latitudine"), "°"), (Loc.N("Longitudine"), "°"), (Loc.N("Altitudine"), "m"), (Loc.N("Accuratezza orizzontale"), "m"), (Loc.N("Velocità"), "m/s")],
        [SensorKind.Proximity] = [(Loc.N("Vicino"), "0–1")],
        // iOS stima i lumen da ARKit, Android misura i lux: i valori dei due sistemi non sono confrontabili.
        [SensorKind.AmbientLight] = [(Loc.N("Intensità"), "lm (iOS) / lx (Android)"), (Loc.N("Temperatura colore"), "K")],
        [SensorKind.DeviceState] = [(Loc.N("Stato termico"), "0–3"), (Loc.N("Batteria"), "0–1")],
        [SensorKind.LidarDepth] = [(Loc.N("Profondità al centro"), "m"), (Loc.N("Pixel validi"), "0–1")],
        [SensorKind.AudioLevel] = [(Loc.N("Livello RMS"), "dBFS"), (Loc.N("Picco"), "0–1")],
        [SensorKind.HeartRate] = [(Loc.N("Frequenza cardiaca"), "bpm"), (Loc.N("Contatto con la pelle"), "0–1"), (Loc.N("Intervallo RR"), "s")],
        [SensorKind.PulseOximeter] = [(Loc.N("SpO2"), "%"), (Loc.N("Frequenza del polso"), "bpm")],
        [SensorKind.BodyTemperature] = [(Loc.N("Temperatura"), "°C")],
    };

    private readonly Dictionary<SensorKind, Channel[]> _streams = [];

    /// <summary>Flussi riassunti da questa classe.</summary>
    public static IReadOnlyCollection<SensorKind> Kinds => Layouts.Keys;

    public void Add(in SensorReading reading)
    {
        if (!Layouts.TryGetValue(reading.Kind, out var layout))
        {
            return;
        }

        if (!_streams.TryGetValue(reading.Kind, out var channels))
        {
            channels = new Channel[layout.Length];
            for (var i = 0; i < channels.Length; i++)
            {
                channels[i] = new Channel();
            }

            _streams[reading.Kind] = channels;
        }

        var values = reading.Values;
        for (var i = 0; i < channels.Length && i < values.Length; i++)
        {
            // CoreLocation segnala con un valore negativo l'accuratezza e la velocità non disponibili.
            if (reading.Kind == SensorKind.Location && i >= 3 && values[i] < 0)
            {
                continue;
            }

            channels[i].Add(values[i]);
        }
    }

    /// <summary>Solo i sensori e i canali che hanno dato almeno un valore valido.</summary>
    public Dictionary<SensorKind, SensorStreamSummary> Build()
    {
        var result = new Dictionary<SensorKind, SensorStreamSummary>();
        foreach (var (kind, channels) in _streams.OrderBy(s => s.Key))
        {
            var layout = Layouts[kind];
            var stats = new List<ChannelStats>(channels.Length);
            for (var i = 0; i < channels.Length; i++)
            {
                var c = channels[i];
                if (c.Stats.Count > 0)
                {
                    stats.Add(new ChannelStats(layout[i].Name, layout[i].Unit, c.Stats.Min, c.Stats.Max, c.Stats.Mean, c.First, c.Last, c.Stats.Count));
                }
            }

            if (stats.Count > 0)
            {
                result[kind] = new SensorStreamSummary(stats);
            }
        }

        return result;
    }

    private sealed class Channel
    {
        public RunningStats Stats;

        public double First { get; private set; }

        public double Last { get; private set; }

        public void Add(float value)
        {
            if (!float.IsFinite(value))
            {
                return;
            }

            if (Stats.Count == 0)
            {
                First = value;
            }

            Last = value;
            Stats.Add(value);
        }
    }
}
