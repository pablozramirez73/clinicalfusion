using System.Text.Json;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>
/// Regressione logistica pre-addestrata su GSTRIDE (163 anziani, 86 caduti nell'ultimo anno), generata da
/// <c>ml/fall_risk/gstride_train.py</c> e incorporata come risorsa. Ogni caratteristica viene sostituita
/// dalla mediana di GSTRIDE se manca, poi standardizzata con media e deviazione standard di GSTRIDE.
/// </summary>
public sealed class FallRiskModel
{
    private const string Resource = "ClinicalSensorFusion.Core.Models.fall_risk_gstride.json";

    private static readonly Lazy<FallRiskModel> Embedded = new(() =>
    {
        using var stream = typeof(FallRiskModel).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Risorsa {Resource} mancante.");
        return Parse(stream);
    });

    private FallRiskModel(string version, string[] features, double[] median, double[] mean, double[] scale, double[] coef,
        double intercept, double auc, double aucLow, double aucHigh, string dataset)
    {
        Version = version;
        Features = features;
        _median = median;
        _mean = mean;
        _scale = scale;
        _coef = coef;
        Intercept = intercept;
        Auc = auc;
        AucLow = aucLow;
        AucHigh = aucHigh;
        Dataset = dataset;
    }

    private readonly double[] _median;
    private readonly double[] _mean;
    private readonly double[] _scale;
    private readonly double[] _coef;

    /// <summary>Il modello incorporato nell'app.</summary>
    public static FallRiskModel Default => Embedded.Value;

    public string Version { get; }

    public IReadOnlyList<string> Features { get; }

    public double Intercept { get; }

    /// <summary>AUC fuori campione su GSTRIDE e il suo intervallo di confidenza al 95%.</summary>
    public double Auc { get; }

    public double AucLow { get; }

    public double AucHigh { get; }

    public string Dataset { get; }

    /// <summary>Probabilità (0–1). Le caratteristiche assenti o non finite prendono la mediana di GSTRIDE.</summary>
    public double Probability(IReadOnlyDictionary<string, double> inputs)
    {
        var z = Intercept;
        for (var i = 0; i < _coef.Length; i++)
        {
            var x = inputs.TryGetValue(Features[i], out var v) && double.IsFinite(v) ? v : _median[i];
            z += _coef[i] * (x - _mean[i]) / (_scale[i] > 0 ? _scale[i] : 1);
        }

        return 1 / (1 + Math.Exp(-z));
    }

    // JsonDocument e non la deserializzazione con i tipi: niente riflessione, quindi nessun problema con il
    // trimming delle build iOS/Android e nessun contesto di serializzazione da aggiornare.
    internal static FallRiskModel Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double[] Numbers(string name) => [.. root.GetProperty(name).EnumerateArray().Select(e => e.GetDouble())];
        var validation = root.GetProperty("validation");
        var ci = validation.GetProperty("auc_ci95");
        var model = new FallRiskModel(
            root.GetProperty("version").GetString() ?? string.Empty,
            [.. root.GetProperty("features").EnumerateArray().Select(e => e.GetString() ?? string.Empty)],
            Numbers("impute_median"), Numbers("mean"), Numbers("scale"), Numbers("coef"),
            root.GetProperty("intercept").GetDouble(),
            validation.GetProperty("auc").GetDouble(), ci[0].GetDouble(), ci[1].GetDouble(),
            root.GetProperty("source").GetProperty("dataset").GetString() ?? string.Empty);
        var n = model.Features.Count;
        if (model._median.Length != n || model._mean.Length != n || model._scale.Length != n || model._coef.Length != n)
        {
            throw new InvalidDataException("Modello del rischio di caduta incoerente: vettori di lunghezza diversa.");
        }

        return model;
    }
}

/// <summary>Stima del rischio di caduta di un soggetto, con le misure da cui viene.</summary>
/// <param name="Probability">Probabilità (0–1) di aver avuto almeno una caduta nell'ultimo anno, secondo il modello.</param>
/// <param name="Inputs">Misure usate, con il nome del modello.</param>
/// <param name="TestsUsed">Test eseguiti da cui vengono le misure.</param>
/// <param name="TestsMissing">Test mai eseguiti dal soggetto: le loro misure valgono la mediana di GSTRIDE.</param>
public sealed record FallRiskEstimate(
    double Probability,
    IReadOnlyDictionary<string, double> Inputs,
    IReadOnlyList<ClinicalTest> TestsUsed,
    IReadOnlyList<ClinicalTest> TestsMissing);

/// <summary>
/// Raccoglie dai test di un soggetto le misure del modello e calcola la stima. Per ogni test vale
/// l'esecuzione più recente; le conversioni sono le stesse di <c>ml/fall_risk/app_export.py</c>.
/// </summary>
public static class FallRisk
{
    /// <summary>I test che alimentano il modello.</summary>
    public static IReadOnlyList<ClinicalTest> Tests { get; } =
        [ClinicalTests.TimedUpAndGo, ClinicalTests.TenMeterWalk, ClinicalTests.FiveTimesSitToStand, ClinicalTests.TandemStance];

    /// <summary>Con meno test la stima dipende soprattutto dalle mediane di GSTRIDE: non la si mostra.</summary>
    public const int MinTests = 2;

    /// <summary>
    /// Stima dalle rilevazioni di un soggetto (tutte, in qualsiasi ordine). Null se i test eseguiti sono meno di
    /// <see cref="MinTests"/> oppure manca sia il TUG sia il cammino, le due misure che pesano di più.
    /// </summary>
    public static FallRiskEstimate? Estimate(IEnumerable<ClinicalSession> sessions, FallRiskModel? model = null)
    {
        var latest = new Dictionary<string, ClinicalSession>();
        foreach (var s in sessions)
        {
            if (s.Summary?.Test is { Value: { } value } test && double.IsFinite(value) && Tests.Any(t => t.Id == test.TestId)
                && (!latest.TryGetValue(test.TestId, out var other) || s.StartedAt > other.StartedAt))
            {
                latest[test.TestId] = s;
            }
        }

        if (latest.Count < MinTests || !(latest.ContainsKey(ClinicalTests.TimedUpAndGo.Id) || latest.ContainsKey(ClinicalTests.TenMeterWalk.Id)))
        {
            return null;
        }

        double? Value(ClinicalTest t) => latest.TryGetValue(t.Id, out var s) ? s.Summary!.Test!.Value : null;
        var inputs = new Dictionary<string, double>();
        if (Value(ClinicalTests.TimedUpAndGo) is { } tug)
        {
            inputs["tug_time_s"] = tug;
        }

        if (Value(ClinicalTests.TenMeterWalk) is { } speed)
        {
            inputs["gait_speed_m_s"] = speed;
        }

        if (Value(ClinicalTests.FiveTimesSitToStand) is { } sts)
        {
            inputs["sppb_chair_index"] = SppbChairIndex(sts);
        }

        if (Value(ClinicalTests.TandemStance) is { } tandem)
        {
            inputs["sppb_balance_index"] = SppbBalanceIndex(tandem);
        }

        // Il ritmo del passo viene dal cammino del 10MWT. GSTRIDE conta le falcate (due passi) con un IMU sul
        // piede: cadenza in falcate = passi / 2, durata della falcata = 120 / passi al minuto, e la variabilità
        // del passo fa da stima di quella della falcata.
        if (latest.TryGetValue(ClinicalTests.TenMeterWalk.Id, out var walk) && walk.Summary?.PoseGait is { } gait && gait.CadenceStepsPerMinute > 0)
        {
            inputs["cadence_strides_min"] = gait.CadenceStepsPerMinute / 2;
            inputs["stride_time_s"] = 120 / gait.CadenceStepsPerMinute;
            inputs["stride_time_cv"] = gait.StepTimeCv;
        }

        model ??= FallRiskModel.Default;
        return new FallRiskEstimate(
            model.Probability(inputs),
            inputs,
            [.. Tests.Where(t => latest.ContainsKey(t.Id))],
            [.. Tests.Where(t => !latest.ContainsKey(t.Id))]);
    }

    /// <summary>Punteggio SPPB dell'alzata (0–4) dal tempo delle 5 alzate, con le soglie di Guralnik.</summary>
    public static double SppbChairIndex(double seconds) =>
        !double.IsFinite(seconds) || seconds > 60 ? 0 : seconds <= 11.19 ? 4 : seconds <= 13.69 ? 3 : seconds <= 16.69 ? 2 : 1;

    /// <summary>
    /// Punteggio SPPB dell'equilibrio (0–4) dal solo tandem, l'unico stadio che l'app esegue: si presume che
    /// chi regge il tandem regga anche piedi uniti e semi-tandem (2 punti); il tandem vale 2 punti a 10 s,
    /// 1 punto da 3 a 10 s, 0 sotto i 3 s.
    /// </summary>
    public static double SppbBalanceIndex(double tandemSeconds) =>
        2 + (tandemSeconds >= 10 ? 2 : tandemSeconds >= 3 ? 1 : 0);
}
