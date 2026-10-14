namespace ClinicalSensorFusion.Core.Sensors;

/// <summary>
/// Un campione di un flusso sensoriale.
/// </summary>
/// <param name="Kind">Flusso di provenienza.</param>
/// <param name="Timestamp">Secondi dall'inizio della rilevazione (orologio monotono).</param>
/// <param name="Values">Valori; layout definito da <see cref="SensorKind"/>. Il chiamante cede la proprietà dell'array.</param>
public readonly record struct SensorReading(SensorKind Kind, double Timestamp, float[] Values);
