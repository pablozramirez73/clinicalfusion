using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>
/// Indicatori sintetici calcolati a fine rilevazione. Viene serializzato in JSON nel DB e inviato al server.
/// Al modello linguistico e nella condivisione va la versione <see cref="WithoutLocation"/>: nessun dato
/// grezzo, nessun identificativo, nessuna posizione.
/// </summary>
public sealed record SessionSummary
{
    public double DurationSeconds { get; init; }

    public CaptureMode Mode { get; init; }

    public Dictionary<Joint, JointRange> JointRanges { get; init; } = [];

    /// <summary>Percentuale di fotogrammi di posa in cui la profondità LiDAR era valida per le articolazioni.</summary>
    public double? LidarCoverage { get; init; }

    public TremorResult? Tremor { get; init; }

    public GaitSummary? Gait { get; init; }

    /// <summary>
    /// Ritmo del passo dai punti della posa (distanza tra le caviglie): c'è anche senza pedometro, quindi
    /// su Android. Null se i passi riconosciuti non bastano.
    /// </summary>
    public PoseGaitSummary? PoseGait { get; init; }

    public AudioFeatures? Audio { get; init; }

    public double? MedianSubjectDistanceMeters { get; init; }

    /// <summary>
    /// Passaggi seduto→in piedi contati dall'angolo di flessione del ginocchio (stima automatica; null senza
    /// fotogrammi di posa). Serve ai test di alzata dalla sedia.
    /// </summary>
    public int? SitToStandCount { get; init; }

    /// <summary>Tocchi pollice–indice rilevati in modalità mani (null se nessuno).</summary>
    public FingerTapSeries? FingerTaps { get; init; }

    /// <summary>Esito del test standardizzato scelto dall'operatore (null per una rilevazione libera).</summary>
    public TestOutcome? Test { get; init; }

    /// <summary>
    /// Ciò che hanno segnalato i sensori ambientali accesi (barometro, GPS, luce, prossimità, stato del
    /// dispositivo, LiDAR, livelli audio, pedometro): statistiche per canale. Null nelle rilevazioni
    /// precedenti all'introduzione del campo.
    /// </summary>
    public Dictionary<SensorKind, SensorStreamSummary>? Sensors { get; init; }

    public Dictionary<SensorKind, long> SampleCounts { get; init; } = [];

    /// <summary>Campioni scartati per saturazione della coda (indice di qualità dell'acquisizione).</summary>
    public long DroppedSamples { get; init; }

    /// <summary>
    /// Copia senza le coordinate GPS: dove è stata fatta la rilevazione (spesso il domicilio del paziente)
    /// identifica la persona, quindi non deve uscire verso il modello linguistico né nella condivisione.
    /// </summary>
    public SessionSummary WithoutLocation() =>
        Sensors is null || !Sensors.ContainsKey(SensorKind.Location)
            ? this
            : this with { Sensors = Sensors.Where(s => s.Key != SensorKind.Location).ToDictionary(s => s.Key, s => s.Value) };
}

/// <summary>Riepilogo del cammino dal pedometro di iOS.</summary>
public sealed record GaitSummary(long Steps, double DistanceMeters, double? CadenceStepsPerMinute, int FloorsAscended, int FloorsDescended);
