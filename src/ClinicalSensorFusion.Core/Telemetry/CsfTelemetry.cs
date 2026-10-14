using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Telemetry;

/// <summary>
/// Punto unico della telemetria di app e server: un <see cref="Meter"/> e una <see cref="ActivitySource"/>
/// con lo stesso nome, più gli strumenti di misura. Usa solo le API standard di .NET
/// (<c>System.Diagnostics</c>): chi ospita il codice decide dove mandare i dati (OpenTelemetry sul server,
/// file di log sul telefono) senza che il Core dipenda da un fornitore.
/// </summary>
/// <remarks>
/// Regole per chi aggiunge strumenti:
/// <list type="bullet">
/// <item>Nomi <c>csf.&lt;area&gt;.&lt;cosa&gt;</c> e unità UCUM (<c>s</c>, <c>By</c>, <c>{sample}</c>), come nelle
/// convenzioni semantiche di OpenTelemetry.</item>
/// <item>Attributi a bassa cardinalità (esito, tipo di flusso, modalità). Mai identificativi di rilevazioni,
/// codici soggetto o nomi: sono dati sanitari e moltiplicherebbero le serie.</item>
/// <item>Le durate sono istogrammi in secondi; gli esiti usano <see cref="Outcomes"/>.</item>
/// </list>
/// </remarks>
public static class CsfTelemetry
{
    public const string Name = "ClinicalSensorFusion";

    private static readonly string? Version = typeof(CsfTelemetry).Assembly.GetName().Version?.ToString();

    public static readonly Meter Meter = new(Name, Version);

    public static readonly ActivitySource Activities = new(Name, Version);

    /// <summary>Valori dell'attributo <c>outcome</c>.</summary>
    public static class Outcomes
    {
        public const string Success = "success";
        public const string Error = "error";
        public const string Cancelled = "cancelled";
        public const string Timeout = "timeout";
    }

    /// <summary>App: avvio e stabilità ("mobile vitals").</summary>
    public static class App
    {
        /// <summary>Dall'avvio del processo alla prima finestra. Oltre 5 s a freddo è "lento" per Android vitals.</summary>
        public static readonly Histogram<double> StartDuration = Meter.CreateHistogram<double>(
            "csf.app.start.duration", "s", "Tempo di avvio dell'app fino alla prima finestra.");

        public static readonly Counter<long> UnhandledExceptions = Meter.CreateCounter<long>(
            "csf.app.unhandled_exceptions", "{exception}", "Eccezioni non gestite (crash o task non osservati).");
    }

    /// <summary>Acquisizione: quanto si registra, quanto si perde, quanto costa scrivere e inferire.</summary>
    public static class Capture
    {
        public static readonly Counter<long> Sessions = Meter.CreateCounter<long>(
            "csf.capture.sessions", "{session}", "Rilevazioni concluse, per modalità.");

        public static readonly Histogram<double> SessionDuration = Meter.CreateHistogram<double>(
            "csf.capture.session.duration", "s", "Durata delle rilevazioni.");

        public static readonly Counter<long> Samples = Meter.CreateCounter<long>(
            "csf.capture.samples", "{sample}", "Campioni salvati, per flusso.");

        /// <summary>Saturazione: il canale del registratore era pieno e il campione è stato scartato.</summary>
        public static readonly Counter<long> DroppedSamples = Meter.CreateCounter<long>(
            "csf.capture.samples.dropped", "{sample}", "Campioni scartati perché il registratore era saturo.");

        public static readonly Histogram<double> ChunkWriteDuration = Meter.CreateHistogram<double>(
            "csf.capture.chunk.write.duration", "s", "Tempo di codifica e scrittura di un blocco nel database.");

        public static readonly Histogram<long> ChunkSize = Meter.CreateHistogram<long>(
            "csf.capture.chunk.size", "By", "Dimensione compressa dei blocchi.");

        public static readonly Counter<long> ChunkWriteErrors = Meter.CreateCounter<long>(
            "csf.capture.chunk.write.errors", "{chunk}", "Blocchi non salvati per un errore di scrittura.");

        /// <summary>Latenza dell'inferenza MediaPipe per fotogramma: determina gli fps effettivi.</summary>
        public static readonly Histogram<double> VisionInferenceDuration = Meter.CreateHistogram<double>(
            "csf.vision.inference.duration", "s", "Durata dell'inferenza MediaPipe su un fotogramma.");
    }

    /// <summary>Sincronizzazione app → server.</summary>
    public static class Sync
    {
        public static readonly Histogram<double> SessionDuration = Meter.CreateHistogram<double>(
            "csf.sync.session.duration", "s", "Durata dell'invio di una rilevazione, per esito.");

        public static readonly Counter<long> Chunks = Meter.CreateCounter<long>(
            "csf.sync.chunks", "{chunk}", "Blocchi inviati al server.");

        public static readonly Counter<long> Bytes = Meter.CreateCounter<long>(
            "csf.sync.bytes", "By", "Byte di blocchi e audio inviati al server.");

        public static readonly Counter<long> AudioFiles = Meter.CreateCounter<long>(
            "csf.sync.audio.files", "{file}", "File audio inviati, o mancanti sul dispositivo (result).");

        /// <summary>Arretrato: se cresce, l'invio non tiene il passo o fallisce.</summary>
        public static readonly Gauge<long> PendingSessions = Meter.CreateGauge<long>(
            "csf.sync.pending_sessions", "{session}", "Rilevazioni concluse ancora da inviare.");
    }

    /// <summary>Analisi con il modello linguistico (Ollama).</summary>
    public static class Ai
    {
        public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
            "csf.ai.request.duration", "s", "Durata di una richiesta di analisi, per esito.");

        public static readonly Histogram<double> TimeToFirstToken = Meter.CreateHistogram<double>(
            "csf.ai.time_to_first_token", "s", "Attesa del primo frammento di risposta.");

        public static readonly Histogram<long> ResponseSize = Meter.CreateHistogram<long>(
            "csf.ai.response.size", "{char}", "Lunghezza delle risposte complete.");
    }

    /// <summary>Server: ingestione e accessi. Latenza, traffico ed errori HTTP arrivano già da ASP.NET Core.</summary>
    public static class Server
    {
        public static readonly Counter<long> IngestedSessions = Meter.CreateCounter<long>(
            "csf.ingest.sessions", "{session}", "Rilevazioni ricevute (result: accepted, denied).");

        public static readonly Counter<long> IngestedChunks = Meter.CreateCounter<long>(
            "csf.ingest.chunks", "{chunk}", "Blocchi ricevuti (result: inserted, duplicate, rejected, denied).");

        public static readonly Counter<long> IngestedBytes = Meter.CreateCounter<long>(
            "csf.ingest.bytes", "By", "Byte accettati, per tipo (kind: chunks, audio).");

        public static readonly Counter<long> Logins = Meter.CreateCounter<long>(
            "csf.auth.logins", "{attempt}", "Accessi alla dashboard (method: cookie, token; outcome).");

        public static readonly Counter<long> RateLimitRejections = Meter.CreateCounter<long>(
            "csf.ratelimit.rejections", "{request}", "Richieste respinte con 429.");
    }

    /// <summary>
    /// Avvia un'operazione misurata: una traccia, un istogramma di durata con l'esito e una riga di log
    /// alla fine. Senza <see cref="TelemetryOperation.Succeed"/> l'esito è <see cref="Outcomes.Error"/>.
    /// </summary>
    public static TelemetryOperation StartOperation(ILogger logger, string name, Histogram<double> duration) =>
        new(logger, name, duration);

    public static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}
