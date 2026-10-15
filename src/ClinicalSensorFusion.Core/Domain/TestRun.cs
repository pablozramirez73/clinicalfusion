using ClinicalSensorFusion.Core.Recording;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>Fase di un test in corso.</summary>
public enum TestRunState
{
    /// <summary>La voce sta spiegando il test: il «Via» non è ancora stato dato.</summary>
    WaitingForStart,

    /// <summary>Dal «Via» in poi: il tempo del test scorre.</summary>
    Running,

    /// <summary>Compito completato o tempo scaduto: la rilevazione va chiusa.</summary>
    Finished,
}

/// <summary>Che cosa fare dopo un passo di <see cref="TestRun.Advance"/>.</summary>
/// <param name="Cue">Frase da dire ora al paziente (tempo residuo, incoraggiamento), se ce n'è una.</param>
/// <param name="Finished">Il test è finito in questo passo: va detto al paziente e la rilevazione va chiusa.</param>
/// <param name="CompletedAt">
/// Istante (secondi di sessione) in cui il paziente ha completato il compito, per i test il cui risultato è
/// un tempo: il tempo del test si ferma lì, non quando la rilevazione viene poi chiusa.
/// </param>
public readonly record struct TestRunStep(string? Cue, bool Finished, double? CompletedAt);

/// <summary>
/// Esecuzione di un test standardizzato dal «Via» alla fine (macchina a stati): frasi previste durante il
/// test, chiusura automatica quando il compito è completato o la durata fissa è scaduta.
/// Non parla e non ferma niente da sé: dice al chiamante che cosa fare a ogni passo, così le regole del
/// protocollo restano fuori dall'interfaccia e si possono verificare senza dispositivo.
/// </summary>
public sealed class TestRun(ClinicalTest test)
{
    private TestCompletionTracker? _completion;
    private int _nextCue;

    public ClinicalTest Test { get; } = test;

    public TestRunState State { get; private set; } = TestRunState.WaitingForStart;

    /// <summary>Tocchi o alzate eseguiti dal «Via»; null se il test non si chiude da solo al completamento.</summary>
    public int? Done => _completion is { } c ? Math.Min(c.Done, c.Required) : null;

    /// <summary>Tocchi o alzate richiesti; null se il test non si chiude da solo al completamento.</summary>
    public int? Required => Test.Completion?.Count;

    /// <summary>
    /// Avanza con il tempo dal «Via» (null finché la guida vocale non l'ha dato) e gli ultimi valori dal vivo.
    /// Va chiamato a intervalli regolari durante la registrazione.
    /// </summary>
    public TestRunStep Advance(double? secondsSinceStart, LiveMetrics? live)
    {
        if (State == TestRunState.Finished || secondsSinceStart is not { } seconds)
        {
            return default;
        }

        State = TestRunState.Running;
        string? cue = null;
        if (_nextCue < Test.Cues.Count && seconds >= Test.Cues[_nextCue].AtSeconds)
        {
            cue = Test.Cues[_nextCue++].Text;
        }

        // I test con un compito riconoscibile (dieci tocchi, cinque alzate, di nuovo seduto) si chiudono da
        // soli quando il paziente lo ha completato. Si conta dal «Via»: il tracker parte alla prima chiamata.
        if (Test.Completion is { } rule && live is not null)
        {
            _completion ??= new TestCompletionTracker(rule);
            if (_completion.Update(live) is { } at)
            {
                State = TestRunState.Finished;
                return new(cue, true, Test.ResultKind == TestResultKind.Seconds ? at : null);
            }
        }

        // I test a durata fissa (30 secondi, 6 minuti, 10 secondi) si chiudono da soli allo scadere.
        if (Test.AutoStopSeconds is { } limit && seconds >= limit)
        {
            State = TestRunState.Finished;
            return new(cue, true, null);
        }

        return new(cue, false, null);
    }
}
