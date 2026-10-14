# Log e telemetria

Un solo framework per app e server, in `src/ClinicalSensorFusion.Core/Telemetry`, costruito sulle API standard
di .NET (`ILogger`, `System.Diagnostics.Metrics`, `ActivitySource`). Il Core non dipende da nessun fornitore:
chi lo ospita decide dove vanno i dati.

| Dove | Log | Metriche e tracce |
|---|---|---|
| App (iOS/Android) | File JSON a rotazione in `Library/logs` (5 file da 2 MB), anche nelle build di rilascio | Aggregate in memoria e scritte nel log quando l'app va in background |
| Server | Console (JSON fuori dallo sviluppo), file a rotazione se `Telemetry:LogDirectory` è impostata | Pagina «Stato del sistema» della dashboard; OpenTelemetry via OTLP se `Telemetry:OtlpEndpoint` è impostato |

L'app **non invia mai** log o metriche da sola: restano nel contenitore dell'app e l'operatore li condivide da
Impostazioni → «Condividi log diagnostici». È una scelta: sono dispositivi che trattano dati sanitari.

## 1. I pezzi

| Tipo | A cosa serve |
|---|---|
| `CsfTelemetry` | `Meter` e `ActivitySource` dell'applicazione e tutti gli strumenti di misura, raggruppati per area. |
| `TelemetryOperation` | Un'operazione con inizio, fine ed esito: durata nell'istogramma (attributo `outcome`), traccia e una riga di log. |
| `MetricsCollector` | Aggregatore in memoria (totale, minimo, massimo, ultimo valore per serie), senza dipendenze. |
| `RollingFileLoggerProvider` | Log su file, una riga JSON per evento, con rotazione e spazio limitato; la scrittura è in background e non blocca mai chi scrive. |
| `LogRedaction` | Regole su cosa può entrare in un log, pseudonimi e pulizia del testo esterno. |

Misurare un'operazione:

```csharp
using var operation = CsfTelemetry.StartOperation(logger, "sync.session", CsfTelemetry.Sync.SessionDuration);
try
{
    await UploadAsync(...);
    operation.Succeed();          // senza questa chiamata l'esito è "error"
}
catch (Exception ex)
{
    operation.Fail(ex, cancelledByCaller: ct.IsCancellationRequested);   // error | timeout | cancelled
    throw;
}
```

## 2. Regole

- **Nei log e negli attributi mai dati del paziente**: niente codice soggetto, nomi, note, testo dei prompt o
  delle analisi, chiavi. Sì a identificativi casuali di rilevazioni e registrazioni, conteggi, durate, esiti.
  Per correlare le righe di uno stesso soggetto: `LogRedaction.Pseudonym(codice)`.
- **Messaggi con segnaposto**, non stringhe interpolate: `LogWarning("Blocco {Kind}#{Seq} fallito", kind, seq)`.
  I valori finiscono in `fields` e si possono filtrare.
- **Nomi delle metriche** `csf.<area>.<cosa>`, unità UCUM (`s`, `By`, `{sample}`), durate in secondi come
  istogrammi: le convenzioni semantiche di OpenTelemetry.
- **Attributi a bassa cardinalità** (esito, tipo di flusso, modalità, modello): mai identificativi.
- Livelli: `Information` per gli eventi attesi, `Warning` per ciò che degrada senza interrompere (campioni
  scartati, invio incompleto, 401/403/429), `Error` per le operazioni fallite, `Critical` per i crash.

## 3. Metriche scelte

Criteri: per il server i «quattro segnali d'oro» (latenza, traffico, errori, saturazione), che coincidono con
il metodo RED per le richieste; per l'app i «mobile vitals» (avvio, stabilità, fluidità); per entrambi le
metriche di dominio che dicono se il dato clinico arriva integro.

### App

| Metrica | Tipo | Perché |
|---|---|---|
| `csf.app.start.duration` | istogramma, s | Tempo di avvio fino alla prima finestra (Android vitals considera lento un avvio a freddo ≥ 5 s). |
| `csf.app.unhandled_exceptions` | contatore | Stabilità: crash e task non osservati. |
| `csf.vision.inference.duration` | istogramma, s | Latenza di MediaPipe per fotogramma: determina gli fps effettivi dell'analisi. |
| `csf.capture.sessions`, `csf.capture.session.duration` | contatore, istogramma | Uso, per modalità. |
| `csf.capture.samples`, `csf.capture.samples.dropped` | contatori | Integrità: i campioni scartati sono saturazione del registratore. |
| `csf.capture.chunk.write.duration`, `.size`, `.write.errors` | istogrammi, contatore | Costo e affidabilità della scrittura nel database cifrato. |
| `csf.sync.session.duration` (per esito) | istogramma, s | Latenza ed errori dell'invio al server. |
| `csf.sync.chunks`, `csf.sync.bytes`, `csf.sync.audio.files` | contatori | Traffico; `result=missing` segnala audio persi sul dispositivo. |
| `csf.sync.pending_sessions` | indicatore | Arretrato: se cresce, l'invio non tiene il passo. |
| `csf.ai.request.duration` (per esito), `csf.ai.time_to_first_token`, `csf.ai.response.size` | istogrammi | Analisi con Ollama: attesa percepita, timeout e risposte troncate. |

### Server

| Metrica | Tipo | Perché |
|---|---|---|
| `http.server.request.duration` (per rotta, metodo, stato) | istogramma, s | Latenza, traffico ed errori di ogni rotta: emessa da ASP.NET Core. |
| `http.server.active_requests` | indicatore | Saturazione. |
| metriche del runtime .NET (GC, heap, thread pool) | varie | Saturazione del processo (solo verso OTLP). |
| `csf.ingest.sessions` (`accepted`, `denied`) | contatore | Rilevazioni ricevute e tentativi su rilevazioni di altri dispositivi. |
| `csf.ingest.chunks` (`inserted`, `duplicate`, `rejected`) | contatore | Blocchi salvati, reinviati, rifiutati perché non validi. |
| `csf.ingest.bytes` (`chunks`, `audio`) | contatore | Volume dei dati e crescita dello spazio. |
| `csf.auth.logins` (metodo, esito) | contatore | Accessi e tentativi falliti. |
| `csf.ratelimit.rejections` | contatore | Richieste respinte con 429. |

## 4. Server

```
Telemetry__OtlpEndpoint=http://otel-collector:4317   # facoltativo: metriche, tracce e log via OTLP (gRPC)
Telemetry__LogDirectory=/var/log/clinical            # facoltativo: log su file (10 file da 10 MB)
```

- Ogni richiesta alle API produce una riga `HTTP {Method} {Route} → {StatusCode} in {DurationMs} ms`; la
  rotta è il modello (`/api/v1/sessions/{id}`), non l'URL.
- La risposta porta `X-Trace-Id`: lo stesso valore è in tutte le righe di log della richiesta.
- Stato del servizio: `/health/live` (il processo risponde), `/health` e `/health/ready` (database e archivio
  audio; anonimi, solo lo stato), `GET /api/v1/system/health` e `/system/metrics` (dettagli, solo clinici).
  La dashboard li mostra in «Stato del sistema».
