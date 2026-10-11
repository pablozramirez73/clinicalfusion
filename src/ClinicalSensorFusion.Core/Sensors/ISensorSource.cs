namespace ClinicalSensorFusion.Core.Sensors;

/// <summary>
/// Sorgente di dati sensoriali di piattaforma (iOS: CoreMotion, ARKit, AVAudioEngine, ...).
/// Le implementazioni pubblicano i campioni su un <see cref="ISensorSink"/> e non devono mai
/// bloccare il thread del sensore: il sink è non bloccante per contratto.
/// </summary>
public interface ISensorSource
{
    /// <summary>Nome leggibile mostrato nella schermata di stato dei sensori.</summary>
    string DisplayName { get; }

    /// <summary>Flussi prodotti da questa sorgente.</summary>
    IReadOnlyList<SensorKind> Produces { get; }

    /// <summary>Indica se l'hardware è presente e il permesso è (o può essere) concesso.</summary>
    bool IsAvailable { get; }

    Task StartAsync(ISensorSink sink, CancellationToken cancellationToken);

    Task StopAsync();
}

/// <summary>Destinazione dei campioni. <see cref="TryWrite"/> non blocca mai; restituisce false se scarta.</summary>
public interface ISensorSink
{
    bool TryWrite(in SensorReading reading);
}
