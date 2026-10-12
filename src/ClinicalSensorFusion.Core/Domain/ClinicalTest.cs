using System.Globalization;

namespace ClinicalSensorFusion.Core.Domain;

/// <summary>Grandezza che un test standardizzato restituisce come punteggio.</summary>
public enum TestResultKind
{
    Seconds,
    Repetitions,
    MetersPerSecond,
    Meters,
}

/// <summary>
/// Test funzionale standardizzato proposto nella schermata di acquisizione: istruzioni per l'operatore,
/// frase da dire al paziente, valori di riferimento e fonte del protocollo.
/// </summary>
/// <param name="AudioGuide">
/// Testo letto ad alta voce al paziente prima del via: spiega con calma che cosa fare. Il conto alla
/// rovescia e il «Via» li aggiunge l'app.
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
    string Source);

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
            _ => v.ToString(culture),
        };
}

/// <summary>
/// Catalogo dei test. Protocolli e soglie vengono da due fonti professionali:
/// CDC STEADI (valutazione del rischio di caduta: TUG, 30-Second Chair Stand, 4-Stage Balance Test) e
/// il "core set" dell'Academy of Neurologic Physical Therapy / APTA (5TSTS, 10MWT, 6MWT).
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
            "Termina la rilevazione quando è di nuovo seduto.",
        ],
        "Quando dico «Via» si alzi, cammini fino alla linea al suo passo normale, si giri, torni alla sedia e si sieda.",
        "Test del cammino con alzata dalla sedia. Si sieda appoggiando la schiena allo schienale. "
        + "Al mio via si alzi, cammini al suo passo normale fino alla linea sul pavimento, si giri, torni alla sedia e si sieda di nuovo. "
        + "Non c'è fretta: cammini come fa di solito.",
        null, TestResultKind.Seconds,
        "≥ 12 s: rischio di caduta aumentato nell'anziano.",
        Cdc);

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
        Cdc);

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
        Cdc);

    public static readonly ClinicalTest FiveTimesSitToStand = new(
        "5tsts", "5TSTS", "Five Times Sit to Stand (5TSTS)",
        "Forza funzionale degli arti inferiori e passaggi posturali.",
        "Sedia standard senza braccioli, contro il muro. iPhone fisso, di lato, con ginocchia e anche inquadrate.",
        [
            "Il paziente siede con le braccia incrociate sul petto e la schiena allo schienale.",
            "Avvia la rilevazione sul «Via».",
            "Il paziente si alza e si siede cinque volte di seguito, il più velocemente possibile.",
            "Termina la rilevazione quando tocca la sedia la quinta volta.",
        ],
        "Si alzi e si sieda cinque volte di seguito, più in fretta che può, alzandosi ogni volta del tutto.",
        "Test delle cinque alzate dalla sedia. Si sieda con la schiena allo schienale e le braccia incrociate sul petto. "
        + "Al mio via si alzi e si sieda cinque volte di seguito, più in fretta che può, alzandosi ogni volta del tutto. "
        + "Alla quinta volta resti seduto.",
        null, TestResultKind.Seconds,
        "> 12 s distingue gli adulti sani dalle persone con ictus; 13 s indica disfunzione dell'equilibrio. Medie nei sani: 60–69 anni 7,8 s · 70–79 9,3 s · 80–85 10,8 s.",
        Anpt);

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
        Anpt);

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
        Anpt);

    /// <summary>Tutti i test si eseguono in modalità corpo (scheletro MediaPipe e sensori di movimento).</summary>
    public static IReadOnlyList<ClinicalTest> All { get; } =
        [TimedUpAndGo, ChairStand30, FiveTimesSitToStand, TandemStance, TenMeterWalk, SixMinuteWalk];

    public static ClinicalTest? Find(string? id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Ricava il punteggio del test dal riepilogo della rilevazione appena conclusa.</summary>
    public static TestOutcome Evaluate(ClinicalTest test, SessionSummary summary)
    {
        var seconds = summary.DurationSeconds;
        TestOutcome Outcome(double? value, bool flag, string interpretation, bool estimated = false) =>
            new(test.Id, test.Name, test.ResultKind, value, estimated, flag, interpretation);

        switch (test.Id)
        {
            case "tug":
                return seconds >= 12
                    ? Outcome(seconds, true, "12 secondi o più: rischio di caduta aumentato nell'anziano.")
                    : Outcome(seconds, false, "Meno di 12 secondi: sotto la soglia di rischio di caduta.");

            case "chair30":
                return Outcome(summary.SitToStandCount, false,
                    "Alzate contate in automatico dall'angolo del ginocchio: verifica con il tuo conteggio e confronta con i valori per età e sesso.",
                    estimated: true);

            case "tandem":
                return seconds < 10
                    ? Outcome(seconds, true, "Meno di 10 secondi: rischio di caduta aumentato nell'anziano.")
                    : Outcome(10, false, "Posizione tenuta per 10 secondi.");

            case "5tsts":
                var counted = summary.SitToStandCount is { } n && n != 5
                    ? string.Create(CultureInfo.InvariantCulture, $" Alzate rilevate in automatico: {n} (attese 5).")
                    : string.Empty;
                return seconds > 12
                    ? Outcome(seconds, true, "Oltre 12 secondi: sopra il cut-off che distingue gli adulti sani; da 13 secondi indica disfunzione dell'equilibrio." + counted)
                    : Outcome(seconds, false, "Entro 12 secondi: sotto il cut-off." + counted);

            case "10mwt":
                if (seconds <= 0)
                {
                    return Outcome(null, false, "Durata non valida.");
                }

                var speed = 6.0 / seconds;
                var category = speed switch
                {
                    < 0.4 => "Sotto 0,4 m/s: deambulazione domestica (classi per l'ictus).",
                    <= 0.8 => "Tra 0,4 e 0,8 m/s: deambulazione comunitaria limitata (classi per l'ictus).",
                    _ => "Oltre 0,8 m/s: deambulazione comunitaria (classi per l'ictus).",
                };
                return Outcome(speed, speed < 0.7,
                    speed < 0.7 ? category + " Sotto 0,7 m/s: rischio aumentato di eventi avversi nell'anziano." : category);

            case "6mwt":
                var distance = summary.Gait is { } gait && double.IsFinite(gait.DistanceMeters) ? gait.DistanceMeters : (double?)null;
                return Outcome(distance, false,
                    distance is null
                        ? "Pedometro non disponibile: usa il conteggio dei giri sul percorso di 12 m."
                        : "Distanza stimata dal pedometro dell'iPhone: il valore ufficiale è il conteggio dei giri sul percorso di 12 m.",
                    estimated: true);

            default:
                return Outcome(null, false, string.Empty);
        }
    }
}
