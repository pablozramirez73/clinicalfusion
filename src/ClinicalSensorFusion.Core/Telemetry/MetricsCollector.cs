using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace ClinicalSensorFusion.Core.Telemetry;

/// <summary>Valore aggregato di una serie (strumento + attributi) dall'avvio del processo.</summary>
/// <param name="Kind"><c>counter</c>, <c>histogram</c> o <c>gauge</c>.</param>
/// <param name="Tags">Attributi nella forma <c>chiave=valore</c>, ordinati.</param>
/// <param name="Count">Numero di misure registrate.</param>
/// <param name="Sum">Somma delle misure: per un contatore è il totale.</param>
/// <param name="Last">Ultima misura: per un indicatore (gauge) è il valore corrente.</param>
public sealed record MetricPoint(string Name, string? Unit, string Kind, string Tags, long Count, double Sum, double Min, double Max, double Last)
{
    public double Mean => Count > 0 ? Sum / Count : 0;
}

/// <summary>
/// Aggregatore in memoria delle metriche del processo, senza dipendenze esterne: serve alla pagina di stato
/// del server e al registro diagnostico dell'app, dove non c'è (e per riservatezza non deve esserci) un
/// servizio di telemetria di terze parti. Non sostituisce un esportatore: tiene solo totali, minimo, massimo
/// e ultimo valore di ogni serie.
/// </summary>
public sealed class MetricsCollector : IDisposable
{
    /// <summary>Tetto alle serie tenute in memoria: oltre, le nuove combinazioni di attributi si ignorano.</summary>
    public const int MaxSeries = 2_000;

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<(Instrument Instrument, string Tags), Aggregate> _series = new();
    private readonly string[] _meterNames;

    /// <param name="meterNames">Nomi dei <see cref="Meter"/> da ascoltare; sempre incluso quello dell'applicazione.</param>
    public MetricsCollector(params string[] meterNames)
    {
        _meterNames = [CsfTelemetry.Name, .. meterNames];
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (Array.IndexOf(_meterNames, instrument.Meter.Name) >= 0)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<float>((i, v, tags, _) => Record(i, v, tags));
        _listener.Start();
    }

    public IReadOnlyList<MetricPoint> Snapshot()
    {
        // Gli strumenti osservabili (es. indicatori del runtime) si leggono solo su richiesta.
        _listener.RecordObservableInstruments();
        var points = new List<MetricPoint>(_series.Count);
        foreach (var ((instrument, tags), aggregate) in _series)
        {
            points.Add(aggregate.ToPoint(instrument, tags));
        }

        points.Sort(static (a, b) =>
        {
            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.Tags, b.Tags);
        });
        return points;
    }

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var key = (instrument, Format(tags));
        if (!_series.TryGetValue(key, out var aggregate))
        {
            if (_series.Count >= MaxSeries)
            {
                return;
            }

            aggregate = _series.GetOrAdd(key, static _ => new Aggregate());
        }

        aggregate.Add(value);
    }

    private static string Format(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.IsEmpty)
        {
            return string.Empty;
        }

        var parts = new string[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            parts[i] = tags[i].Key + "=" + Convert.ToString(tags[i].Value, CultureInfo.InvariantCulture);
        }

        Array.Sort(parts, StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    private sealed class Aggregate
    {
        private readonly Lock _gate = new();
        private long _count;
        private double _sum;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;
        private double _last;

        public void Add(double value)
        {
            lock (_gate)
            {
                _count++;
                _sum += value;
                _min = Math.Min(_min, value);
                _max = Math.Max(_max, value);
                _last = value;
            }
        }

        public MetricPoint ToPoint(Instrument instrument, string tags)
        {
            var type = instrument.GetType();
            var kind = !type.IsGenericType ? "gauge" : type.GetGenericTypeDefinition().Name switch
            {
                var n when n.StartsWith("Counter", StringComparison.Ordinal) || n.StartsWith("ObservableCounter", StringComparison.Ordinal)
                    || n.StartsWith("UpDownCounter", StringComparison.Ordinal) || n.StartsWith("ObservableUpDownCounter", StringComparison.Ordinal) => "counter",
                var n when n.StartsWith("Histogram", StringComparison.Ordinal) => "histogram",
                _ => "gauge",
            };

            lock (_gate)
            {
                return new MetricPoint(instrument.Name, instrument.Unit, kind, tags, _count, _sum, _min, _max, _last);
            }
        }
    }

    /// <summary>Una riga compatta per il log: <c>nome{attributi} n=… sum=… max=…</c>.</summary>
    public static string Describe(IReadOnlyList<MetricPoint> points)
    {
        var text = new StringBuilder();
        foreach (var p in points)
        {
            text.Append(p.Name);
            if (p.Tags.Length > 0)
            {
                text.Append('{').Append(p.Tags).Append('}');
            }

            text.Append(CultureInfo.InvariantCulture, $" n={p.Count}");
            text.Append(p.Kind switch
            {
                "counter" => string.Create(CultureInfo.InvariantCulture, $" total={p.Sum:0.###}"),
                "histogram" => string.Create(CultureInfo.InvariantCulture, $" mean={p.Mean:0.###} max={p.Max:0.###}"),
                _ => string.Create(CultureInfo.InvariantCulture, $" value={p.Last:0.###}"),
            });
            text.Append("; ");
        }

        return text.ToString();
    }
}
