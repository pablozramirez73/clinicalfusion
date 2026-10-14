using System.Globalization;
using ClinicalSensorFusion.Core.Localization;
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
    /// <summary>
    /// Copia con i testi nella lingua dell'interfaccia, per mostrarli all'operatore e leggerli al paziente. Il
    /// riepilogo salvato porta sempre il nome originale (vedi <see cref="ClinicalTests.Evaluate"/>).
    /// </summary>
    public ClinicalTest Localized() => this with
    {
        Name = Loc.T(Name),
        Purpose = Loc.T(Purpose),
        Setup = Loc.T(Setup),
        Steps = [.. Steps.Select(step => Loc.T(step))],
        PatientScript = Loc.T(PatientScript),
        AudioGuide = Loc.T(AudioGuide),
        Reference = Loc.T(Reference),
        Source = Loc.T(Source),
        Cues = [.. Cues.Select(cue => cue with { Text = Loc.T(cue.Text) })],
    };

    /// <summary>Frasi dette durante il test, a un certo numero di secondi dal «Via» (incoraggiamenti, tempo residuo).</summary>
    public IReadOnlyList<TestCue> Cues { get; init; } = [];

    /// <summary>Modalità di acquisizione in cui il test si esegue.</summary>
    public CaptureMode Mode { get; init; } = CaptureMode.BodyLidar;

    /// <summary>
    /// Compito il cui completamento chiude da solo il test (null: lo chiude l'operatore o la durata fissa).
    /// È un aiuto: se l'app non riconosce il movimento, l'operatore termina a mano come sempre.
    /// </summary>
    public TestCompletion? Completion { get; init; }
}

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
            TestResultKind.Repetitions => string.Format(culture, Loc.T("{0:F0} alzate"), v),
            TestResultKind.MetersPerSecond => string.Format(culture, "{0:F2} m/s", v),
            TestResultKind.Meters => string.Format(culture, "{0:F0} m", v),
            TestResultKind.Hertz => string.Format(culture, Loc.T("{0:F1} tocchi/s"), v),
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
        "tug", "TUG", Loc.N("Timed Up and Go (TUG)"),
        Loc.N("Mobilità e rischio di caduta."),
        Loc.N("Sedia con braccioli e una linea a 3 metri. iPhone fisso, a 3–4 m di lato, con tutto il percorso inquadrato."),
        [
            Loc.N("Il paziente siede appoggiato allo schienale, con le sue calzature abituali; può usare l'ausilio."),
            Loc.N("Avvia la rilevazione sul «Via»."),
            Loc.N("Il paziente si alza, cammina fino alla linea a passo normale, si gira, torna e si siede."),
            Loc.N("Quando è di nuovo seduto la rilevazione si chiude da sola; se non lo riconosce, termina tu."),
        ],
        Loc.N("Quando dico «Via» si alzi, cammini fino alla linea al suo passo normale, si giri, torni alla sedia e si sieda."),
        Loc.N("Test del cammino con alzata dalla sedia. Si sieda appoggiando la schiena allo schienale. "
        + "Al mio via si alzi, cammini al suo passo normale fino alla linea sul pavimento, si giri, torni alla sedia e si sieda di nuovo. "
        + "Non c'è fretta: cammini come fa di solito."),
        null, TestResultKind.Seconds,
        Loc.N("≥ 12 s: rischio di caduta aumentato nell'anziano."),
        Cdc)
    {
        Completion = new(TestCompletionKind.RisesThenSeated, 1, SeatedHoldSeconds: 1),
    };

    public static readonly ClinicalTest ChairStand30 = new(
        "chair30", "Chair Stand 30 s", Loc.N("30-Second Chair Stand"),
        Loc.N("Forza e resistenza degli arti inferiori."),
        Loc.N("Sedia senza braccioli con schienale dritto (seduta a circa 43 cm). iPhone fisso, di lato, con ginocchia e anche inquadrate."),
        [
            Loc.N("Il paziente siede al centro della sedia, braccia incrociate sul petto, piedi a terra."),
            Loc.N("Avvia la rilevazione sul «Via»: si ferma da sola dopo 30 secondi."),
            Loc.N("Conta le alzate complete; se a 30 secondi è oltre metà salita, l'alzata vale."),
            Loc.N("Se deve aiutarsi con le braccia, interrompi: il punteggio è 0."),
        ],
        Loc.N("Al «Via» si alzi completamente in piedi e si risieda, più volte che può in 30 secondi, senza usare le braccia."),
        Loc.N("Test delle alzate dalla sedia in trenta secondi. Si sieda al centro della sedia, con i piedi ben appoggiati a terra "
        + "e le braccia incrociate sul petto. Al mio via si alzi completamente in piedi e poi si risieda, più volte che può, "
        + "senza aiutarsi con le braccia. Le dirò io quando fermarsi."),
        30, TestResultKind.Repetitions,
        Loc.N("Sotto la media (uomini / donne): 60–64 anni < 14 / < 12 · 65–69 < 12 / < 11 · 70–74 < 12 / < 10 · 75–79 < 11 / < 10 · 80–84 < 10 / < 9 · 85–89 < 8 / < 8 · 90–94 < 7 / < 4."),
        Cdc)
    {
        Cues =
        [
            new(15, Loc.N("Continui così, è a metà.")),
            new(25, Loc.N("Ancora cinque secondi.")),
        ],
    };

    public static readonly ClinicalTest TandemStance = new(
        "tandem", "Tandem 10 s", Loc.N("Equilibrio in tandem (4-Stage Balance Test)"),
        Loc.N("Equilibrio statico."),
        Loc.N("Paziente senza ausili, occhi aperti. Resta al suo fianco. iPhone fisso, di fronte, a figura intera."),
        [
            Loc.N("Le quattro posizioni, in ordine: piedi uniti · semi-tandem · tandem (tallone contro punta) · su un piede."),
            Loc.N("Si passa alla posizione successiva solo se regge 10 secondi senza muovere i piedi né appoggiarsi."),
            Loc.N("Aiutalo a mettersi in posizione; quando è stabile lascialo e avvia la rilevazione."),
            Loc.N("Si ferma da sola a 10 secondi. Se perde l'equilibrio prima, termina tu: il tempo è il risultato."),
        ],
        Loc.N("Resti in questa posizione per 10 secondi. Può allargare le braccia o muovere il corpo, ma non sposti i piedi."),
        Loc.N("Test dell'equilibrio. Resti in piedi nella posizione che le è stata mostrata, con gli occhi aperti. "
        + "Può allargare le braccia o muovere il corpo per tenersi in equilibrio, ma non sposti i piedi. "
        + "Tenga la posizione finché non le dico stop."),
        10, TestResultKind.Seconds,
        Loc.N("Tandem tenuto meno di 10 s: rischio di caduta aumentato nell'anziano."),
        Cdc)
    {
        Cues =
        [
            new(5, Loc.N("Bene, ancora cinque secondi.")),
        ],
    };

    public static readonly ClinicalTest FiveTimesSitToStand = new(
        "5tsts", "5TSTS", Loc.N("Five Times Sit to Stand (5TSTS)"),
        Loc.N("Forza funzionale degli arti inferiori e passaggi posturali."),
        Loc.N("Sedia standard senza braccioli, contro il muro. iPhone fisso, di lato, con ginocchia e anche inquadrate."),
        [
            Loc.N("Il paziente siede con le braccia incrociate sul petto e la schiena allo schienale."),
            Loc.N("Avvia la rilevazione sul «Via»."),
            Loc.N("Il paziente si alza e si siede cinque volte di seguito, il più velocemente possibile."),
            Loc.N("Quando si siede la quinta volta la rilevazione si chiude da sola; se non lo riconosce, termina tu."),
        ],
        Loc.N("Si alzi e si sieda cinque volte di seguito, più in fretta che può, alzandosi ogni volta del tutto."),
        Loc.N("Test delle cinque alzate dalla sedia. Si sieda con la schiena allo schienale e le braccia incrociate sul petto. "
        + "Al mio via si alzi e si sieda cinque volte di seguito, più in fretta che può, alzandosi ogni volta del tutto. "
        + "Alla quinta volta resti seduto."),
        null, TestResultKind.Seconds,
        Loc.N("> 12 s distingue gli adulti sani dalle persone con ictus; 13 s indica disfunzione dell'equilibrio. Medie nei sani: 60–69 anni 7,8 s · 70–79 9,3 s · 80–85 10,8 s."),
        Anpt)
    {
        Completion = new(TestCompletionKind.RisesThenSeated, 5, SeatedHoldSeconds: 0.3),
    };

    public static readonly ClinicalTest TenMeterWalk = new(
        "10mwt", "10MWT", Loc.N("10 Meter Walk Test (10MWT)"),
        Loc.N("Velocità del cammino."),
        Loc.N("Percorso di 10 m con segni a 2 m e a 8 m. Cammina mezzo passo dietro al paziente, senza dargli il ritmo."),
        [
            Loc.N("Il paziente parte dall'inizio del percorso e cammina fino in fondo."),
            Loc.N("Avvia la rilevazione quando il primo piede supera il segno dei 2 m."),
            Loc.N("Termina la rilevazione quando il primo piede supera il segno degli 8 m."),
            Loc.N("L'app calcola la velocità sui 6 m centrali. Ripeti due volte a passo comodo e due a passo veloce."),
        ],
        Loc.N("Cammini al suo passo comodo e si fermi quando arriva al segno in fondo."),
        Loc.N("Test della velocità del cammino. Si metta all'inizio del percorso. "
        + "Al mio via cammini al suo passo comodo fino al segno in fondo, senza fermarsi prima."),
        null, TestResultKind.MetersPerSecond,
        Loc.N("Ictus: < 0,4 m/s deambulazione domestica · 0,4–0,8 comunitaria limitata · > 0,8 comunitaria. Anziani sani: < 0,7 m/s rischio aumentato di eventi avversi."),
        Anpt);

    public static readonly ClinicalTest SixMinuteWalk = new(
        "6mwt", "6MWT", Loc.N("6 Minute Walk Test (6MWT)"),
        Loc.N("Resistenza e capacità di cammino."),
        Loc.N("Percorso di 12 m con due coni. L'iPhone va addosso al paziente (tasca o cintura): la distanza viene dal pedometro."),
        [
            Loc.N("Il paziente cammina avanti e indietro tra i coni per 6 minuti; può rallentare o fermarsi."),
            Loc.N("Avvia la rilevazione sul «Via»: si ferma da sola dopo 6 minuti."),
            Loc.N("Conta i giri: il valore ufficiale è giri × 12 m più il tratto parziale."),
            Loc.N("La distanza mostrata dall'app è una stima da confrontare con il conteggio dei giri."),
        ],
        Loc.N("Cammini per sei minuti coprendo più strada che può. Può rallentare o fermarsi se serve, poi riprenda appena riesce."),
        Loc.N("Test del cammino di sei minuti. Al mio via cammini avanti e indietro tra i due coni, coprendo più strada che può. "
        + "Può rallentare o fermarsi a riposare se ne ha bisogno, e riprendere appena riesce. Le dirò io quando sono passati i sei minuti."),
        360, TestResultKind.Meters,
        Loc.N("Medie negli anziani autonomi (uomini / donne): 60–69 anni 572 / 538 m · 70–79 527 / 471 m · 80–89 417 / 392 m."),
        Anpt)
    {
        Cues =
        [
            new(60, Loc.N("Sta andando bene. Mancano cinque minuti.")),
            new(120, Loc.N("Continui così. Mancano quattro minuti.")),
            new(180, Loc.N("Sta andando bene. È a metà.")),
            new(240, Loc.N("Continui così. Mancano solo due minuti.")),
            new(300, Loc.N("Sta andando bene. Manca un minuto.")),
        ],
    };

    public static readonly ClinicalTest FingerTapping = new(
        "ftap", "Finger Tapping", Loc.N("Finger Tapping (MDS-UPDRS 3.4)"),
        Loc.N("Velocità e ampiezza del movimento fine: tocco ripetuto dell'indice sul pollice (bradicinesia)."),
        Loc.N("Una mano alla volta. iPhone fisso a 30–40 cm, con pollice e indice visti di lato e tutta la mano nell'inquadratura."),
        [
            Loc.N("Mostra tu il movimento, poi smetti: il paziente non deve copiarti durante la prova."),
            Loc.N("Il paziente tiene la mano ferma davanti alla fotocamera, pollice e indice ben visibili."),
            Loc.N("Avvia la rilevazione: dopo il «Via» tocca l'indice sul pollice 10 volte, più veloce e più ampio che può."),
            Loc.N("Al decimo tocco la rilevazione si chiude da sola (se non li riconosce, termina tu). Ripeti con l'altra mano in una nuova rilevazione."),
        ],
        Loc.N("Tocchi l'indice sul pollice dieci volte, più velocemente e più ampiamente che può."),
        Loc.N("Test del tocco delle dita. Tenga la mano ferma davanti al telefono, con il pollice e l'indice ben visibili. "
        + "Al mio via tocchi la punta dell'indice sulla punta del pollice dieci volte di seguito, "
        + "più velocemente che può e aprendo le dita il più possibile ogni volta."),
        null, TestResultKind.Hertz,
        Loc.N("Nella MDS-UPDRS il clinico assegna un punteggio da 0 a 4 valutando velocità, ampiezza, esitazioni, arresti e riduzione progressiva dell'ampiezza. L'app misura queste grandezze, non assegna il punteggio."),
        Loc.N("MDS-UPDRS Parte III, item 3.4 — Movement Disorder Society"))
    {
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
        // Il nome resta quello originale anche se il test arriva tradotto (Localized): è un dato del riepilogo,
        // inviato al server. L'interpretazione invece è testo per l'operatore, nella lingua dell'interfaccia.
        var name = Find(test.Id)?.Name ?? test.Name;
        TestOutcome Outcome(double? value, bool flag, string interpretation, bool estimated = false) =>
            new(test.Id, name, test.ResultKind, value, estimated, flag, interpretation);

        if (testSeconds is not { } seconds)
        {
            return Outcome(null, false, Loc.T("Rilevazione terminata prima del «Via»: nessun punteggio."));
        }

        switch (test.Id)
        {
            case "tug":
                return seconds >= 12
                    ? Outcome(seconds, true, Loc.T("12 secondi o più: rischio di caduta aumentato nell'anziano."))
                    : Outcome(seconds, false, Loc.T("Meno di 12 secondi: sotto la soglia di rischio di caduta."));

            case "chair30":
                return Outcome(summary.SitToStandCount, false,
                    Loc.T("Alzate contate in automatico dall'angolo del ginocchio: verifica con il tuo conteggio e confronta con i valori per età e sesso."),
                    estimated: true);

            case "tandem":
                return seconds < 10
                    ? Outcome(seconds, true, Loc.T("Meno di 10 secondi: rischio di caduta aumentato nell'anziano."))
                    : Outcome(10, false, Loc.T("Posizione tenuta per 10 secondi."));

            case "5tsts":
                var counted = summary.SitToStandCount is { } n && n != 5
                    ? " " + Loc.F("Alzate rilevate in automatico: {0} (attese 5).", n)
                    : string.Empty;
                return seconds > 12
                    ? Outcome(seconds, true, Loc.T("Oltre 12 secondi: sopra il cut-off che distingue gli adulti sani; da 13 secondi indica disfunzione dell'equilibrio.") + counted)
                    : Outcome(seconds, false, Loc.T("Entro 12 secondi: sotto il cut-off.") + counted);

            case "10mwt":
                if (seconds <= 0)
                {
                    return Outcome(null, false, Loc.T("Durata non valida."));
                }

                var speed = 6.0 / seconds;
                var category = speed switch
                {
                    < 0.4 => Loc.T("Sotto 0,4 m/s: deambulazione domestica (classi per l'ictus)."),
                    <= 0.8 => Loc.T("Tra 0,4 e 0,8 m/s: deambulazione comunitaria limitata (classi per l'ictus)."),
                    _ => Loc.T("Oltre 0,8 m/s: deambulazione comunitaria (classi per l'ictus)."),
                };
                return Outcome(speed, speed < 0.7,
                    speed < 0.7 ? category + " " + Loc.T("Sotto 0,7 m/s: rischio aumentato di eventi avversi nell'anziano.") : category);

            case "ftap":
                // Contano solo i tocchi dopo il «Via»: prima la voce sta ancora spiegando.
                var taps = FingerTapMetrics.From(
                    summary.FingerTaps ?? new FingerTapSeries([], []),
                    summary.DurationSeconds - seconds);
                if (taps.Taps == 0)
                {
                    return Outcome(null, false, Loc.T("Nessun tocco rilevato: controlla che pollice e indice siano ben visibili di lato."), estimated: true);
                }

                var parts = new List<string> { Loc.F("Tocchi rilevati: {0}.", taps.Taps) };
                if (taps.MeanAmplitude is { } amplitude)
                {
                    parts.Add(Loc.F("Ampiezza media: {0:F0}% della lunghezza del palmo.", amplitude * 100));
                }

                if (taps.AmplitudeDecrement is { } decrement)
                {
                    parts.Add(decrement > 0.05
                        ? Loc.F("Ampiezza in calo del {0:F0}% tra i primi e gli ultimi tre tocchi.", decrement * 100)
                        : Loc.T("Ampiezza mantenuta dall'inizio alla fine."));
                }

                if (taps.IntervalVariability is { } variability)
                {
                    parts.Add(Loc.F("Irregolarità del ritmo: {0:F0}%.", variability * 100));
                }

                parts.Add(Loc.T("Misure automatiche dai punti della mano: da verificare a vista."));
                return Outcome(taps.FrequencyHz, false, string.Join(" ", parts), estimated: true);

            case "6mwt":
                var distance = summary.Gait is { } gait && double.IsFinite(gait.DistanceMeters) ? gait.DistanceMeters : (double?)null;
                return Outcome(distance, false,
                    distance is null
                        ? Loc.T("Pedometro non disponibile: usa il conteggio dei giri sul percorso di 12 m.")
                        : Loc.T("Distanza stimata dal pedometro dell'iPhone: il valore ufficiale è il conteggio dei giri sul percorso di 12 m."),
                    estimated: true);

            default:
                return Outcome(null, false, string.Empty);
        }
    }
}
