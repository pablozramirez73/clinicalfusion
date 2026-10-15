using System.Globalization;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>Grandezza che un test standardizzato restituisce come punteggio.</summary>
public enum TestResultKind
{
    Seconds,
    Repetitions,
    MetersPerSecond,
    Meters,

    /// <summary>Tocchi al secondo.</summary>
    Hertz,
}

/// <summary>
/// Test funzionale standardizzato proposto nella schermata di acquisizione: istruzioni per l'operatore,
/// frase da dire al paziente, valori di riferimento e fonte del protocollo.
/// </summary>
/// <param name="AudioGuide">
/// Testo letto ad alta voce al paziente prima del via: spiega con calma che cosa fare. Il conto alla
/// rovescia e il «Via» li aggiunge l'app. La registrazione è già in corso mentre viene letta: il tempo del
/// test si conta dal «Via».
/// </param>
/// <param name="AutoStopSeconds">Durata fissa del test: la registrazione si ferma da sola allo scadere.</param>
public sealed record ClinicalTest(
    string Id,
    string ShortName,
    string Name,
    string Purpose,
    string Setup,
    IReadOnlyList<string> Steps,
    string PatientScript,
    string AudioGuide,
    double? AutoStopSeconds,
    TestResultKind ResultKind,
    string Reference,
    string Source)
{
    /// <summary>Frasi dette durante il test, a un certo numero di secondi dal «Via» (incoraggiamenti, tempo residuo).</summary>
    public IReadOnlyList<TestCue> Cues { get; init; } = [];

    /// <summary>Modalità di acquisizione in cui il test si esegue.</summary>
    public CaptureMode Mode { get; init; } = CaptureMode.BodyLidar;

    /// <summary>
    /// Compito il cui completamento chiude da solo il test (null: lo chiude l'operatore o la durata fissa).
    /// È un aiuto: se l'app non riconosce il movimento, l'operatore termina a mano come sempre.
    /// </summary>
    public TestCompletion? Completion { get; init; }

    /// <summary>
    /// Come si ricava il punteggio dal riepilogo (Strategy). Obbligatorio: un test nuovo non può finire
    /// senza punteggio perché ci si è dimenticati di aggiungerlo altrove.
    /// </summary>
    public required TestScoring Scoring { get; init; }
}

/// <summary>Calcola il punteggio di un test.</summary>
/// <param name="summary">Riepilogo della rilevazione appena conclusa.</param>
/// <param name="testSeconds">Secondi trascorsi dal «Via» alla fine del test.</param>
public delegate TestScore TestScoring(SessionSummary summary, double testSeconds);

/// <summary>Punteggio prodotto da <see cref="TestScoring"/>; <see cref="ClinicalTests.Evaluate"/> lo completa con i dati del test.</summary>
public sealed record TestScore(double? Value, bool Flag, string Interpretation, bool Estimated = false);

/// <summary>Che cosa deve aver fatto il paziente, dal «Via», perché il test si consideri eseguito.</summary>
public enum TestCompletionKind
{
    /// <summary>Un certo numero di tocchi indice-pollice.</summary>
    FingerTaps,

    /// <summary>Un certo numero di alzate dalla sedia, e poi di nuovo seduto.</summary>
    RisesThenSeated,
}

/// <param name="Count">Tocchi o alzate richiesti.</param>
/// <param name="SeatedHoldSeconds">
/// Per quanto deve restare seduto perché il test si chiuda: evita di scambiare per "seduto" un ginocchio
/// piegato durante il cammino. Il tempo del test si ferma comunque all'istante in cui si è seduto.
/// </param>
public sealed record TestCompletion(TestCompletionKind Kind, int Count, double SeatedHoldSeconds = 0);

/// <summary>
/// Segue l'esecuzione di un test dal «Via» e dice quando il compito è stato completato. Conta solo ciò
/// che avviene dopo la prima chiamata: i movimenti fatti mentre la voce spiega il test non valgono.
/// </summary>
public sealed class TestCompletionTracker(TestCompletion rule)
{
    private int? _baseline;

    /// <summary>Tocchi o alzate eseguiti dal «Via».</summary>
    public int Done { get; private set; }

    public int Required => rule.Count;

    /// <summary>
    /// Aggiorna con gli ultimi valori dal vivo. Restituisce l'istante (secondi di sessione) in cui il
    /// compito è stato completato, o null se non lo è ancora.
    /// </summary>
    public double? Update(Recording.LiveMetrics live)
    {
        var total = rule.Kind == TestCompletionKind.FingerTaps ? live.FingerTaps : live.SitToStandCount;
        _baseline ??= total;
        Done = total - _baseline.Value;
        if (Done < rule.Count)
        {
            return null;
        }

        if (rule.Kind == TestCompletionKind.FingerTaps)
        {
            return live.Elapsed;
        }

        return live.SeatedSince is { } since && live.Elapsed - since >= rule.SeatedHoldSeconds ? since : null;
    }
}

/// <summary>Frase pronunciata durante un test, <paramref name="AtSeconds"/> secondi dopo il «Via».</summary>
public sealed record TestCue(double AtSeconds, string Text);

/// <summary>Esito di un test, salvato nel riepilogo della rilevazione.</summary>
/// <param name="Value">Punteggio; null se non ricavabile dai dati (per esempio pedometro assente).</param>
/// <param name="Estimated">Il valore è una stima automatica che l'operatore deve verificare.</param>
/// <param name="Flag">Il valore supera una soglia di attenzione riportata dalla fonte.</param>
public sealed record TestOutcome(
    string TestId,
    string TestName,
    TestResultKind Kind,
    double? Value,
    bool Estimated,
    bool Flag,
    string Interpretation)
{
    public string Format(CultureInfo culture) => Value is not { } v || !double.IsFinite(v)
        ? "—"
        : Kind switch
        {
            TestResultKind.Seconds => string.Format(culture, "{0:F1} s", v),
            TestResultKind.Repetitions => string.Format(culture, "{0:F0} alzate", v),
            TestResultKind.MetersPerSecond => string.Format(culture, "{0:F2} m/s", v),
            TestResultKind.Meters => string.Format(culture, "{0:F0} m", v),
            TestResultKind.Hertz => string.Format(culture, "{0:F1} tocchi/s", v),
            _ => v.ToString(culture),
        };
}

/// <summary>
/// Catalogo dei test. Protocolli e soglie vengono da due fonti professionali:
/// CDC STEADI (valutazione del rischio di caduta: TUG, 30-Second Chair Stand, 4-Stage Balance Test) e
/// il "core set" dell'Academy of Neurologic Physical Therapy / APTA (5TSTS, 10MWT, 6MWT).
/// Per le mani: Finger Tapping, item 3.4 della MDS-UPDRS (Movement Disorder Society).
/// Le soglie sono riportate come nelle fonti: servono a orientare, non sostituiscono la valutazione clinica.
/// </summary>
public static class ClinicalTests
{
    private const string Cdc = "CDC STEADI — cdc.gov/steadi";
    private const string Anpt = "Core set ANPT / APTA — neuropt.org";

    public static readonly ClinicalTest TimedUpAndGo = new(
        "tug", "TUG", "Timed Up and Go (TUG)",
        "Mobilità e rischio di caduta.",
        "Sedia con braccioli e una linea a 3 metri. iPhone fisso, a 3–4 m di lato, con tutto il percorso inquadrato.",
        [
            "Il paziente siede appoggiato allo schienale, con le sue calzature abituali; può usare l'ausilio.",
            "Avvia la rilevazione sul «Via».",
            "Il paziente si alza, cammina fino alla linea a passo normale, si gira, torna e si siede.",
            "Quando è di nuovo seduto la rilevazione si chiude da sola; se non lo riconosce, termina tu.",
        ],
        "Quando dico «Via» si alzi, cammini fino alla linea al suo passo normale, si giri, torni alla sedia e si sieda.",
        "Test del cammino con alzata dalla sedia. Si sieda appoggiando la schiena allo schienale. "
        + "Al mio via si alzi, cammini al suo passo normale fino alla linea sul pavimento, si giri, torni alla sedia e si sieda di nuovo. "
        + "Non c'è fretta: cammini come fa di solito.",
        null, TestResultKind.Seconds,
        "≥ 12 s: rischio di caduta aumentato nell'anziano.",
        Cdc)
    {
        Scoring = ScoreTimedUpAndGo,
        Completion = new(TestCompletionKind.RisesThenSeated, 1, SeatedHoldSeconds: 1),
    };

    public static readonly ClinicalTest ChairStand30 = new(
        "chair30", "Chair Stand 30 s", "30-Second Chair Stand",
        "Forza e resistenza degli arti inferiori.",
        "Sedia senza braccioli con schienale dritto (seduta a circa 43 cm). iPhone fisso, di lato, con ginocchia e anche inquadrate.",
        [
            "Il paziente siede al centro della sedia, braccia incrociate sul petto, piedi a terra.",
            "Avvia la rilevazione sul «Via»: si ferma da sola dopo 30 secondi.",
            "Conta le alzate complete; se a 30 secondi è oltre metà salita, l'alzata vale.",
            "Se deve aiutarsi con le braccia, interrompi: il punteggio è 0.",
        ],
        "Al «Via» si alzi completamente in piedi e si risieda, più volte che può in 30 secondi, senza usare le braccia.",
        "Test delle alzate dalla sedia in trenta secondi. Si sieda al centro della sedia, con i piedi ben appoggiati a terra "
        + "e le braccia incrociate sul petto. Al mio via si alzi completamente in piedi e poi si risieda, più volte che può, "
        + "senza aiutarsi con le braccia. Le dirò io quando fermarsi.",
        30, TestResultKind.Repetitions,
        "Sotto la media (uomini / donne): 60–64 anni < 14 / < 12 · 65–69 < 12 / < 11 · 70–74 < 12 / < 10 · 75–79 < 11 / < 10 · 80–84 < 10 / < 9 · 85–89 < 8 / < 8 · 90–94 < 7 / < 4.",
        Cdc)
    {
        Scoring = ScoreChairStand30,
        Cues =
        [
            new(15, "Continui così, è a metà."),
            new(25, "Ancora cinque secondi."),
        ],
    };

    public static readonly ClinicalTest TandemStance = new(
        "tandem", "Tandem 10 s", "Equilibrio in tandem (4-Stage Balance Test)",
        "Equilibrio statico.",
        "Paziente senza ausili, occhi aperti. Resta al suo fianco. iPhone fisso, di fronte, a figura intera.",
        [
            "Le quattro posizioni, in ordine: piedi uniti · semi-tandem · tandem (tallone contro punta) · su un piede.",
            "Si passa alla posizione successiva solo se regge 10 secondi senza muovere i piedi né appoggiarsi.",
            "Aiutalo a mettersi in posizione; quando è stabile lascialo e avvia la rilevazione.",
            "Si ferma da sola a 10 secondi. Se perde l'equilibrio prima, termina tu: il tempo è il risultato.",
        ],
        "Resti in questa posizione per 10 secondi. Può allargare le braccia o muovere il corpo, ma non sposti i piedi.",
        "Test dell'equilibrio. Resti in piedi nella posizione che le è stata mostrata, con gli occhi aperti. "
        + "Può allargare le braccia o muovere il corpo per tenersi in equilibrio, ma non sposti i piedi. "
        + "Tenga la posizione finché non le dico stop.",
        10, TestResultKind.Seconds,
        "Tandem tenuto meno di 10 s: rischio di caduta aumentato nell'anziano.",
        Cdc)
    {
        Scoring = ScoreTandemStance,
        Cues =
        [
            new(5, "Bene, ancora cinque secondi."),
        ],
    };

    public static readonly ClinicalTest FiveTimesSitToStand = new(
        "5tsts", "5TSTS", "Five Times Sit to Stand (5TSTS)",
        "Forza funzionale degli arti inferiori e passaggi posturali.",
        "Sedia standard senza braccioli, contro il muro. iPhone fisso, di lato, con ginocchia e anche inquadrate.",
        [
            "Il paziente siede con le braccia incrociate sul petto e la schiena allo schienale.",
            "Avvia la rilevazione sul «Via».",
            "Il paziente si alza e si siede cinque volte di seguito, il più velocemente possibile.",
            "Quando si siede la quinta volta la rilevazione si chiude da sola; se non lo riconosce, termina tu.",
        ],
        "Si alzi e si sieda cinque volte di seguito, più in fretta che può, alzandosi ogni volta del tutto.",
        "Test delle cinque alzate dalla sedia. Si sieda con la schiena allo schienale e le braccia incrociate sul petto. "
        + "Al mio via si alzi e si sieda cinque volte di seguito, più in fretta che può, alzandosi ogni volta del tutto. "
        + "Alla quinta volta resti seduto.",
        null, TestResultKind.Seconds,
        "> 12 s distingue gli adulti sani dalle persone con ictus; 13 s indica disfunzione dell'equilibrio. Medie nei sani: 60–69 anni 7,8 s · 70–79 9,3 s · 80–85 10,8 s.",
        Anpt)
    {
        Scoring = ScoreFiveTimesSitToStand,
        Completion = new(TestCompletionKind.RisesThenSeated, 5, SeatedHoldSeconds: 0.3),
    };

    public static readonly ClinicalTest TenMeterWalk = new(
        "10mwt", "10MWT", "10 Meter Walk Test (10MWT)",
        "Velocità del cammino.",
        "Percorso di 10 m con segni a 2 m e a 8 m. Cammina mezzo passo dietro al paziente, senza dargli il ritmo.",
        [
            "Il paziente parte dall'inizio del percorso e cammina fino in fondo.",
            "Avvia la rilevazione quando il primo piede supera il segno dei 2 m.",
            "Termina la rilevazione quando il primo piede supera il segno degli 8 m.",
            "L'app calcola la velocità sui 6 m centrali. Ripeti due volte a passo comodo e due a passo veloce.",
        ],
        "Cammini al suo passo comodo e si fermi quando arriva al segno in fondo.",
        "Test della velocità del cammino. Si metta all'inizio del percorso. "
        + "Al mio via cammini al suo passo comodo fino al segno in fondo, senza fermarsi prima.",
        null, TestResultKind.MetersPerSecond,
        "Ictus: < 0,4 m/s deambulazione domestica · 0,4–0,8 comunitaria limitata · > 0,8 comunitaria. Anziani sani: < 0,7 m/s rischio aumentato di eventi avversi.",
        Anpt)
    {
        Scoring = ScoreTenMeterWalk,
    };

    public static readonly ClinicalTest SixMinuteWalk = new(
        "6mwt", "6MWT", "6 Minute Walk Test (6MWT)",
        "Resistenza e capacità di cammino.",
        "Percorso di 12 m con due coni. L'iPhone va addosso al paziente (tasca o cintura): la distanza viene dal pedometro.",
        [
            "Il paziente cammina avanti e indietro tra i coni per 6 minuti; può rallentare o fermarsi.",
            "Avvia la rilevazione sul «Via»: si ferma da sola dopo 6 minuti.",
            "Conta i giri: il valore ufficiale è giri × 12 m più il tratto parziale.",
            "La distanza mostrata dall'app è una stima da confrontare con il conteggio dei giri.",
        ],
        "Cammini per sei minuti coprendo più strada che può. Può rallentare o fermarsi se serve, poi riprenda appena riesce.",
        "Test del cammino di sei minuti. Al mio via cammini avanti e indietro tra i due coni, coprendo più strada che può. "
        + "Può rallentare o fermarsi a riposare se ne ha bisogno, e riprendere appena riesce. Le dirò io quando sono passati i sei minuti.",
        360, TestResultKind.Meters,
        "Medie negli anziani autonomi (uomini / donne): 60–69 anni 572 / 538 m · 70–79 527 / 471 m · 80–89 417 / 392 m.",
        Anpt)
    {
        Scoring = ScoreSixMinuteWalk,
        Cues =
        [
            new(60, "Sta andando bene. Mancano cinque minuti."),
            new(120, "Continui così. Mancano quattro minuti."),
            new(180, "Sta andando bene. È a metà."),
            new(240, "Continui così. Mancano solo due minuti."),
            new(300, "Sta andando bene. Manca un minuto."),
        ],
    };

    public static readonly ClinicalTest FingerTapping = new(
        "ftap", "Finger Tapping", "Finger Tapping (MDS-UPDRS 3.4)",
        "Velocità e ampiezza del movimento fine: tocco ripetuto dell'indice sul pollice (bradicinesia).",
        "Una mano alla volta. iPhone fisso a 30–40 cm, con pollice e indice visti di lato e tutta la mano nell'inquadratura.",
        [
            "Mostra tu il movimento, poi smetti: il paziente non deve copiarti durante la prova.",
            "Il paziente tiene la mano ferma davanti alla fotocamera, pollice e indice ben visibili.",
            "Avvia la rilevazione: dopo il «Via» tocca l'indice sul pollice 10 volte, più veloce e più ampio che può.",
            "Al decimo tocco la rilevazione si chiude da sola (se non li riconosce, termina tu). Ripeti con l'altra mano in una nuova rilevazione.",
        ],
        "Tocchi l'indice sul pollice dieci volte, più velocemente e più ampiamente che può.",
        "Test del tocco delle dita. Tenga la mano ferma davanti al telefono, con il pollice e l'indice ben visibili. "
        + "Al mio via tocchi la punta dell'indice sulla punta del pollice dieci volte di seguito, "
        + "più velocemente che può e aprendo le dita il più possibile ogni volta.",
        null, TestResultKind.Hertz,
        "Nella MDS-UPDRS il clinico assegna un punteggio da 0 a 4 valutando velocità, ampiezza, esitazioni, arresti e riduzione progressiva dell'ampiezza. L'app misura queste grandezze, non assegna il punteggio.",
        "MDS-UPDRS Parte III, item 3.4 — Movement Disorder Society")
    {
        Scoring = ScoreFingerTapping,
        Mode = CaptureMode.HandsLidar,
        Completion = new(TestCompletionKind.FingerTaps, 10),
    };

    public static IReadOnlyList<ClinicalTest> All { get; } =
        [TimedUpAndGo, ChairStand30, FiveTimesSitToStand, TandemStance, TenMeterWalk, SixMinuteWalk, FingerTapping];

    /// <summary>Test eseguibili in una modalità di acquisizione (corpo: test funzionali; mani: finger tapping).</summary>
    public static IReadOnlyList<ClinicalTest> For(CaptureMode mode) => [.. All.Where(t => t.Mode == mode)];

    public static ClinicalTest? Find(string? id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Ricava il punteggio del test dal riepilogo della rilevazione appena conclusa.</summary>
    /// <param name="testSeconds">
    /// Secondi trascorsi dal «Via» alla fine. La registrazione comincia prima, mentre la voce spiega il test:
    /// quel tratto non conta. Null se la rilevazione è stata chiusa prima del «Via».
    /// </param>
    public static TestOutcome Evaluate(ClinicalTest test, SessionSummary summary, double? testSeconds)
    {
        var score = testSeconds is { } seconds
            ? test.Scoring(summary, seconds)
            : new TestScore(null, false, "Rilevazione terminata prima del «Via»: nessun punteggio.");
        return new(test.Id, test.Name, test.ResultKind, score.Value, score.Estimated, score.Flag, score.Interpretation);
    }

    private static TestScore ScoreTimedUpAndGo(SessionSummary summary, double seconds) => seconds >= 12
        ? new(seconds, true, "12 secondi o più: rischio di caduta aumentato nell'anziano.")
        : new(seconds, false, "Meno di 12 secondi: sotto la soglia di rischio di caduta.");

    private static TestScore ScoreChairStand30(SessionSummary summary, double seconds) =>
        new(summary.SitToStandCount, false,
            "Alzate contate in automatico dall'angolo del ginocchio: verifica con il tuo conteggio e confronta con i valori per età e sesso.",
            Estimated: true);

    private static TestScore ScoreTandemStance(SessionSummary summary, double seconds) => seconds < 10
        ? new(seconds, true, "Meno di 10 secondi: rischio di caduta aumentato nell'anziano.")
        : new(10, false, "Posizione tenuta per 10 secondi.");

    private static TestScore ScoreFiveTimesSitToStand(SessionSummary summary, double seconds)
    {
        var counted = summary.SitToStandCount is { } n && n != 5
            ? string.Create(CultureInfo.InvariantCulture, $" Alzate rilevate in automatico: {n} (attese 5).")
            : string.Empty;
        return seconds > 12
            ? new(seconds, true, "Oltre 12 secondi: sopra il cut-off che distingue gli adulti sani; da 13 secondi indica disfunzione dell'equilibrio." + counted)
            : new(seconds, false, "Entro 12 secondi: sotto il cut-off." + counted);
    }

    private static TestScore ScoreTenMeterWalk(SessionSummary summary, double seconds)
    {
        if (seconds <= 0)
        {
            return new(null, false, "Durata non valida.");
        }

        var speed = 6.0 / seconds;
        var category = speed switch
        {
            < 0.4 => "Sotto 0,4 m/s: deambulazione domestica (classi per l'ictus).",
            <= 0.8 => "Tra 0,4 e 0,8 m/s: deambulazione comunitaria limitata (classi per l'ictus).",
            _ => "Oltre 0,8 m/s: deambulazione comunitaria (classi per l'ictus).",
        };
        return new(speed, speed < 0.7,
            speed < 0.7 ? category + " Sotto 0,7 m/s: rischio aumentato di eventi avversi nell'anziano." : category);
    }

    private static TestScore ScoreFingerTapping(SessionSummary summary, double seconds)
    {
        // Contano solo i tocchi dopo il «Via»: prima la voce sta ancora spiegando.
        var taps = FingerTapMetrics.From(
            summary.FingerTaps ?? new FingerTapSeries([], []),
            summary.DurationSeconds - seconds);
        if (taps.Taps == 0)
        {
            return new(null, false, "Nessun tocco rilevato: controlla che pollice e indice siano ben visibili di lato.", Estimated: true);
        }

        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"Tocchi rilevati: {taps.Taps}.") };
        if (taps.MeanAmplitude is { } amplitude)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Ampiezza media: {amplitude * 100:F0}% della lunghezza del palmo."));
        }

        if (taps.AmplitudeDecrement is { } decrement)
        {
            parts.Add(decrement > 0.05
                ? string.Create(CultureInfo.InvariantCulture, $"Ampiezza in calo del {decrement * 100:F0}% tra i primi e gli ultimi tre tocchi.")
                : "Ampiezza mantenuta dall'inizio alla fine.");
        }

        if (taps.IntervalVariability is { } variability)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Irregolarità del ritmo: {variability * 100:F0}%."));
        }

        parts.Add("Misure automatiche dai punti della mano: da verificare a vista.");
        return new(taps.FrequencyHz, false, string.Join(" ", parts), Estimated: true);
    }

    private static TestScore ScoreSixMinuteWalk(SessionSummary summary, double seconds)
    {
        var distance = summary.Gait is { } gait && double.IsFinite(gait.DistanceMeters) ? gait.DistanceMeters : (double?)null;
        return new(distance, false,
            distance is null
                ? "Pedometro non disponibile: usa il conteggio dei giri sul percorso di 12 m."
                : "Distanza stimata dal pedometro dell'iPhone: il valore ufficiale è il conteggio dei giri sul percorso di 12 m.",
            Estimated: true);
    }
}
