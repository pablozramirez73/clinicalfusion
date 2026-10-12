using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>
/// Indicatori sintetici calcolati a fine rilevazione. Viene serializzato in JSON nel DB e
/// costituisce l'unico contenuto inviato al modello linguistico (nessun dato grezzo, nessun identificativo).
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

    public AudioFeatures? Audio { get; init; }

    public double? MedianSubjectDistanceMeters { get; init; }

    /// <summary>
    /// Passaggi seduto→in piedi contati dall'angolo di flessione del ginocchio (stima automatica; null senza
    /// fotogrammi di posa). Serve ai test di alzata dalla sedia.
    /// </summary>
    public int? SitToStandCount { get; init; }

    /// <summary>Esito del test standardizzato scelto dall'operatore (null per una rilevazione libera).</summary>
    public TestOutcome? Test { get; init; }

    public Dictionary<SensorKind, long> SampleCounts { get; init; } = [];

    /// <summary>Campioni scartati per saturazione della coda (indice di qualità dell'acquisizione).</summary>
    public long DroppedSamples { get; init; }
}

/// <summary>Riepilogo del cammino dal pedometro di iOS.</summary>
public sealed record GaitSummary(long Steps, double DistanceMeters, double? CadenceStepsPerMinute, int FloorsAscended, int FloorsDescended);
