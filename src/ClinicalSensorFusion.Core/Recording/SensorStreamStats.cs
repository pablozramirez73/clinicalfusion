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
        [SensorKind.Barometer] = [("Altitudine relativa", "m"), ("Pressione", "kPa")],
        [SensorKind.Pedometer] = [("Passi", ""), ("Distanza", "m"), ("Cadenza", "passi/s"), ("Piani saliti", ""), ("Piani scesi", "")],
        [SensorKind.Location] = [("Latitudine", "°"), ("Longitudine", "°"), ("Altitudine", "m"), ("Accuratezza orizzontale", "m"), ("Velocità", "m/s")],
        [SensorKind.Proximity] = [("Vicino", "0–1")],
        // iOS stima i lumen da ARKit, Android misura i lux: i valori dei due sistemi non sono confrontabili.
        [SensorKind.AmbientLight] = [("Intensità", "lm (iOS) / lx (Android)"), ("Temperatura colore", "K")],
        [SensorKind.DeviceState] = [("Stato termico", "0–3"), ("Batteria", "0–1")],
        [SensorKind.LidarDepth] = [("Profondità al centro", "m"), ("Pixel validi", "0–1")],
        [SensorKind.AudioLevel] = [("Livello RMS", "dBFS"), ("Picco", "0–1")],
        [SensorKind.HeartRate] = [("Frequenza cardiaca", "bpm"), ("Contatto con la pelle", "0–1"), ("Intervallo RR", "s")],
        [SensorKind.PulseOximeter] = [("SpO2", "%"), ("Frequenza del polso", "bpm")],
        [SensorKind.BodyTemperature] = [("Temperatura", "°C")],
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
