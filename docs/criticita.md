# Criticità e verifiche

Revisione tecnica del repository: problemi di sicurezza, robustezza e incoerenze tra codice e
documentazione. Non è un documento di conformità e non sostituisce una verifica a runtime.

> **Aggiornamento del 13/10.** Mentre la revisione era in corso il repository è avanzato di **8 commit**
> (dalle 07:46 alle 21:04). Molte voci di questo documento sono state chiuse da quei commit: ognuna
> porta ora il suo stato, il commit che l'ha chiusa e la riga di codice che lo dimostra. Le voci
> **aperte** sono state riverificate sul codice attuale, non su quello di quando furono scritte.

## Stato del repository al momento dell'aggiornamento

Indice `codegraph` risincronizzato: **206 file, 3.846 nodi, 7.936 archi** in 11,79 MB.
Nessuna riga di codice è stata modificata da questa revisione.

| area | file | righe |
|---|---|---|
| App MAUI | 82 | 8.797 |
| Core | 52 | 5.462 |
| Server | 31 | 5.080 |
| test | 18 | 2.934 |
| Dashboard | 25 | 1.814 |
| MediaPipe.Android (Java) | 3 | 416 |
| native (Swift) | 3 | 316 |
| MediaPipe (binding C#) | 2 | 109 |

C#: 175 file, 21.127 righe. Test: 122 casi, contro i "38" ancora dichiarati in
`docs/architettura.md:153`.

Aree nate dopo la prima stesura di questo documento, non coperte dalla revisione:

- **`Core/Localization/`** — traduzione in stile gettext: la chiave è il testo italiano scritto nel
  codice, le traduzioni sono JSON incorporati, una voce mancante ricade sull'italiano. Più
  `App/Localization/TrExtension.cs`.
- **`Core/Bluetooth/`** e `Platforms/iOS/Bluetooth/IosBluetoothSensors.cs` — frequenza cardiaca,
  saturimetro, termometro.
- **Server** — `FhirEndpoints` (HL7 FHIR R4 in sola lettura ed esportazione), `AuditEndpoints`,
  `SystemEndpoints`, `ApiDocs`, `JwtTokenService`, `AudioStorageHealthCheck`, `RateLimitOptions`.
- **`Core/Telemetry/`** — framework di log e metriche.

## Come leggere le voci

**Stato:**

- **risolto** — chiuso da un commit, con la riga di codice che lo dimostra.
- **aperto** — riverificato sul codice attuale: il problema c'è ancora.
- **accettato** — il comportamento è cambiato ed è ora una scelta documentata nel codice.
- **da verificare** — non ho potuto confermarlo.

**Verifica:** *verificato* (ho letto il codice), *dall'indice* (ricognizione con lettura parziale),
*dedotto* (ragionamento, non provato).

Le posizioni `file:riga` sono riferite alla radice del repository e si riferiscono al codice attuale.

### Un limite dello strumento, da tenere presente

`codegraph callers` **non vede le chiamate ai metodi statici**: risponde "No callers found" per
`ClinicalPromptBuilder`, che invece è chiamato in
`src/ClinicalSensorFusion.App/ViewModels/SessionDetailViewModel.cs:165` e `:313` (verificato con
`grep`). Un `callers` vuoto non è una prova di assenza.

---

## 1. Sicurezza

### 1.1 Scoping per dispositivo nell'ingest — gravità alta · **risolto** in `66e9cc0` · verificato

Qualunque chiave di dispositivo valida poteva fare upsert di **qualunque** identificativo di sessione e
sostituirne l'audio. L'identità del dispositivo non veniva mai letta e la sessione era cercata solo per
`Id`.

**Com'è ora:** esiste un proprietario. `Services/IngestService.cs:34` confronta
`session.OwnerDevice == DeviceName(device)`, il proprietario è assegnato al primo upsert (`:48`) e
ricavato dai claim (`:170`). `docs/server.md:41` documenta il comportamento: metadati, blocchi e audio
di una rilevazione altrui sono rifiutati con 409/404 e il tentativo finisce nel registro come
`ingest.denied`. La stessa pagina (`:51-53`) chiarisce che il proprietario è il **nome** del
dispositivo, quindi rinominare una voce equivale a cambiare dispositivo, e che le rilevazioni ricevute
prima del vincolo vengono adottate dal primo dispositivo che le reinvia.

### 1.2 Decompressione senza limite sui blocchi dei sensori — gravità alta · **risolto** in `66e9cc0` · verificato

`SensorChunkCodec.Decode` decomponeva il payload Brotli in un `MemoryStream` senza tetto, e il ciclo di
parsing non controllava i limiti. Il server memorizza i blocchi grezzi e li decodifica sul percorso di
lettura del clinico (`Services/SessionAnalytics.cs:38`): un payload ostile esplodeva in memoria aprendo
`/pose`.

**Com'è ora:** esiste un tetto esplicito. `Recording/SensorChunkCodec.cs:66` definisce
`DefaultMaxDecodedBytes = 8 * 1024 * 1024`, `Decode` lo accetta come parametro (`:69`) e `CountSamples`
(`:95`) permette di contare i campioni senza decodificare. Il commento a `:64` spiega proprio il rischio
che aveva motivato la voce. `docs/server.md:43` documenta anche il limite dei lotti a 16 MB.

### 1.3 Rate limiting solo sul login — gravità media · **risolto** in `66e9cc0` · verificato

Esisteva una sola policy (`login`, 5/minuto per IP) e nessun limite globale.

**Com'è ora:** le rotte di ingest hanno policy proprie — `ingest` e `ingest-audio`, applicate in
`Endpoints/IngestEndpoints.cs:31,35,39` — e i limiti sono configurabili dalla sezione `RateLimits`
(`Program.cs:101`). `docs/server.md:43` dichiara limiti su **tutte** le rotte: 600 richieste/minuto per
IP, 300/minuto per dispositivo in ingestione, 2 caricamenti audio contemporanei, con `429` e
`Retry-After`.

### 1.4 `X-Forwarded-For` non validato — gravità media · **aperto** · dedotto

`Program.cs:147-148` continua a svuotare `KnownIPNetworks` e `KnownProxies`. La differenza rispetto
alla prima stesura è che ora c'è un commento che lo giustifica ("dietro un reverse proxy: Cloudflare
Tunnel, nginx").

Il rischio resta quello di prima ed è condizionato al dispiegamento: senza proxy noti il middleware non
valida la catena, quindi l'header è falsificabile da chi raggiunge il servizio direttamente, e il rate
limit per IP diventa aggirabile. Con il servizio esposto solo attraverso il tunnel è teorico; esposto
diretto non lo è. Non provato qui: *dedotto* dal comportamento documentato del middleware.

### 1.5 Autocreate dei pazienti non validata — gravità media · **aperto** · verificato

`IngestEndpoints.cs:53` valida ancora solo spaziatura e lunghezza massima (64) del codice soggetto. La
validazione più stretta che rifiuta i caratteri di controllo resta solo su `PatientInput.Validate`
(`PatientEndpoints.cs`), cioè sul percorso della dashboard e non su quello dell'ingestione.

### 1.6 Registro di audit cieco — gravità media · **risolto** in `66e9cc0` · verificato

Il registro era scrivibile e non leggibile, i login falliti e il logout non erano tracciati, e le voci
non contenevano né IP né user-agent.

**Com'è ora:** tutto e tre le cose.

- `Endpoints/AuditEndpoints.cs` espone la consultazione, in sola lettura, e `AuditItem` porta
  `IpAddress` e `UserAgent`.
- I login falliti sono tracciati: `audit.Record(http.User, "auth.login.failed", …)` in
  `Endpoints/AuthEndpoints.cs:43` (dashboard) e `:71` (JWT).
- Il logout è tracciato: `audit.Record(http.User, "auth.logout")` in `AuthEndpoints.cs:92`.

### 1.7 Scritture su richieste di lettura — gravità bassa · **aperto** · verificato

`SaveChangesAsync` è ancora invocato su percorsi di lettura: `Endpoints/SessionEndpoints.cs:138,160,189`
e `Endpoints/PatientEndpoints.cs:84,107`. Il motivo è il registro di audit scritto sullo stesso
`DbContext`. Resta il fatto che una GET scrive, quindi non è idempotente e ha un costo di scrittura.

### 1.8 Altre voci

- **`/health` anonimo — accettato, e ora anche limitato.** Ci sono `/health/live` (il processo risponde),
  `/health` e `/health/ready` (dipendenze raggiungibili), con un `/health` autenticato nel gruppo di sistema.
  Gli anonimi espongono "solo lo stato di ogni controllo", scelta dichiarata in
  `Endpoints/SystemEndpoints.cs:21-22`. Restava però il **costo illimitato dei controlli**, che l'anonimato
  da solo non copriva: ogni richiesta a `/health` o `/health/ready` apriva una connessione al database e
  **scriveva e cancellava un file** (`Services/AudioStorageHealthCheck.cs`), e le due rotte erano
  `DisableRateLimiting()`, cioè fuori anche dal limite globale. Un flood anonimo era amplificazione.
  **Risolto** in `bf261af`: le due rotte passano da `DisableRateLimiting()` a una policy dedicata per
  indirizzo IP (`RateLimits:HealthPerMinute`, 60/minuto), mentre `/health/live` — che non esegue controlli —
  resta esente perché è la sonda degli orchestratori. Attenzione al meccanismo: la policy nominata **si somma**
  al limite globale, quindi il tetto effettivo per IP è il più stretto dei due.
- **Schema `http` ammesso per il server di sincronizzazione — aperto.** `App/Services/SyncService.cs:97`
  accetta ancora sia `UriSchemeHttps` sia `UriSchemeHttp`: la chiave del dispositivo può viaggiare in
  chiaro se configurata male.
- **Revoca di un dispositivo — da verificare.** `Security/SecurityOptions.cs` non espone flag di
  scadenza: la revoca resta la rimozione della voce più il ricaricamento. `docs/server.md:41` la
  documenta come tale e spiega che per sostituire una chiave compromessa basta cambiare `KeySha256`
  lasciando il nome. Da intendersi come limite dichiarato, non come difetto.

---

## 2. Robustezza e correttezza

### 2.1 L'audio mancante non bloccava la sincronizzazione — gravità media · **risolto** in `66e9cc0` · verificato

`SessionSynchronizer` registrava un warning se il file audio non esisteva, proseguiva e marcava
comunque la sessione come sincronizzata.

**Com'è ora:** è l'opposto. Il commento a `Sync/SessionSynchronizer.cs:175` dice "Mai 'sincronizzata'
con audio mancante: l'operatore deve saperlo", il caso solleva `FileNotFoundException` (`:177`) e
`MarkSyncedAsync` viene chiamato solo dopo il successo (`:182`).

### 2.2 Stream del modello troncato salvato come risposta valida — gravità media · **risolto** in `66e9cc0` · verificato

Il ciclo si chiudeva quando il flusso finiva, senza verificare di aver ricevuto il frammento finale.

**Com'è ora:** la fine è verificata. `Ai/OllamaClient.cs:122` controlla `chunk?.Done == true` e il
commento a `:128` descrive il caso ("Fine dello stream senza 'done': connessione chiusa a metà —
proxy, rete, Ollama riavviato"). Il commento a `:35` elenca esplicitamente errore del server, timeout
di rete e stream chiuso prima del frammento finale.

### 2.3 Timeout di rete indistinguibile dall'annullamento — gravità media · **risolto** in `66e9cc0` · verificato

Un timeout dell'`HttpClient` arrivava come `OperationCanceledException` e finiva nello stesso ramo
dell'annullamento volontario, scrivendo `"[interrotta]"`.

**Com'è ora:** il ramo dell'annullamento ha un filtro che lo distingue:
`ViewModels/SessionDetailViewModel.cs:343` cattura `OperationCanceledException` **quando**
`_analysisCts.IsCancellationRequested`, e un timeout non soddisfa la condizione: ricade nel ramo
generico a `:347`, che tratta `OllamaException`, `HttpRequestException` e `OperationCanceledException`.

### 2.4 Migrazioni non coperte — gravità media · **risolto** in `9726fe2` · verificato

Nessun test riferiva `Migrations`.

**Com'è ora:** `tests/ClinicalSensorFusion.Core.Tests/PersistenceTests.cs` le copre. Il commit
`9726fe2` intitola la cosa "test delle migrazioni".

### 2.5 Copertura LiDAR = 0 invece di "non applicabile" — gravità bassa · **risolto** in `9726fe2` · verificato

Il valore era calcolato come `_lidarPoseFrames / _poseFrames`, e su una piattaforma senza LiDAR il
numeratore restava a zero: il riepilogo mostrava **0%**, cioè un difetto di qualità del dato, invece di
"non applicabile".

**Com'è ora:** `Recording/SessionMetricsAggregator.cs:212` è
`lidarUsed && _poseFrames > 0 ? _lidarPoseFrames / (double)_poseFrames : null`. E la documentazione è
stata allineata: `docs/android.md:17` ora dice "copertura assente dal riepilogo (non 0%)".

### 2.6 Altre voci

- **`AudioStorage` controllava il limite dopo aver scritto tutto — risolto** in `66e9cc0`. Il controllo
  è ora dentro il ciclo di scrittura (`Services/AudioStorage.cs:63-71`), con il commento "Il limite si
  applica mentre si scrive: un file fuori misura non deve prima riempire il disco".
- **Gate di sincronizzazione asimmetrico — aperto.** `App/Services/SyncService.cs:31` usa
  `WaitAsync(0, ct)` (non attende) mentre `:55` usa `WaitAsync(ct)` (attende): un invio singolo può
  restare accodato dietro una sincronizzazione completa.
- **`/dashboard/overview` senza paginazione — accettato.** Carica in memoria le sessioni della finestra
  e deserializza ogni `SummaryJson` (`Endpoints/DashboardEndpoints.cs:44-48`), ma ora il commento a
  `:50` dichiara la scelta ("poche centinaia di righe, query semplice e portabile"). Da rivedere se il
  dataset cresce.
- **Modello predefinito `gemma4:e4b-mlx` — aperto.** Invariato in `Core/Settings/AppSettings.cs:26`.
  Non corrisponde a una famiglia Ollama nota: sembra un segnaposto o un refuso. Se il modello non
  esiste sul server, la lista lo sostituisce col primo disponibile ma la configurazione salvata resta.
- **Chiave del database in una stringa managed — aperto.** Invariato:
  `Persistence/ClinicalDatabase.cs:103` interpola la chiave in `PRAGMA key = "x'{hex}'"` e `Array.Clear`
  a `:105` è solo sul percorso di successo. La `string` non è azzerabile e resta nell'heap fino alla
  raccolta.
- **Chiave errata riconosciuta solo dal codice 26 — aperto.** Invariato:
  `Persistence/ClinicalDatabase.cs:118` intercetta solo `SQLITE_NOTADB`; altre varianti emergerebbero
  come `SqliteException` grezza.
- **iOS, chiave esistente di lunghezza diversa da 32 — aperto.** `Platforms/iOS/Security/KeychainKeyProvider.cs:30`
  accetta il record solo con `ValueData is { Length: 32 }`. Se il record esiste con altra lunghezza lo
  stato è `Success`, quindi il controllo a `:35` non scatta e si genera una chiave nuova: la scrittura
  successiva va in conflitto e l'errore è "Impossibile salvare la chiave nel Portachiavi" invece di una
  diagnosi. Il guard è migliorato per gli altri stati, questo caso no.
- **Android, alias Keystore orfano — aperto** · dedotto. `Platforms/Android/Security/KeystoreKeyProvider.cs:42`
  decide su `File.Exists(WrappedKeyPath)` e a `:51` chiama `CreateWrappingKey()`: se il file avvolto è
  perso ma l'alias resta, la rigenerazione può fallire. Non provato a runtime.

### 2.7 Troncamento silenzioso e `total` falso nell'interfaccia FHIR — gravità alta · **risolto** in `bf261af` · verificato

`/fhir/Observation` e `/fhir/Patient/{id}/$everything` costruivano le risorse in memoria e si fermavano a
`Take(MaxSessionsScanned)` = 2000 rilevazioni. Oltre quel numero i risultati erano **troncati senza alcun
avviso**, e `SearchObservationsAsync` passava `all.Count` come `total`: il conteggio dei match *fra le sole
2000 scansionate*. Un `searchset.total` falso, visto che la specifica lo definisce come il numero di
risultati, e un `link.next` che non poteva andare oltre le 2000. La dashboard usa `$everything` come download
in un colpo solo, e `docs/fhir.md` la chiama "esportazione completa di un paziente": un clinico riceveva una
cartella incompleta che sembrava intera. In più ogni richiesta, anche con `_count=1`, materializzava e
deserializzava fino a 2000 `SummaryJson`.

**Com'è ora:** mai più dati incompleti presentati come completi.

- **Ricerca Observation** (`Endpoints/FhirEndpoints.cs:285`): un gate in SQL conta le rilevazioni del filtro
  **prima** di materializzare; oltre il tetto risponde `413` con OperationOutcome `too-costly` e dice di
  restringere con `patient` o `date`. Sotto il tetto `total` è esatto (`:317`) e la paginazione via `link.next`
  funziona. `code` e `category` vivono nel riepilogo e non in colonne, quindi il conteggio veritiero impone di
  leggere tutte le rilevazioni del filtro SQL: è il tetto a rendere il costo prevedibile.
- **`$everything`** (`:229`): conteggio in SQL, rifiuto esplicito oltre il tetto e scansione in streaming
  (`AsAsyncEnumerable`). Nessun `Take`: l'esportazione è completa.
- I tetti sono configurabili — `Fhir:MaxSessionsScanned` (2000) e `Fhir:MaxSessionsPerExport` (5000) — e il
  `CapabilityStatement` li dichiara, insieme a `_count` e `_offset`, che erano supportati ma non annunciati.
- Nello stesso commit: `TrySessions` (`:397`) con una data che porta anche l'ora faceva equivalere `gt` a `ge`.
- Test: sei casi nuovi (61 in totale), fra cui l'esportazione completa con 2001 rilevazioni e il `413` oltre i
  due tetti.

---

## 3. Codice esposto e mai usato

- **`GET /api/v1/sessions/{id}/streams/{kind}` e il tipo `StreamSeries` — aperto.** La dashboard non li
  chiama: nessun riferimento a `streams/` in `src/ClinicalSensorFusion.Dashboard/src`.
- **`GET /api/v1/sessions` (lista globale)** — la dashboard passa solo da `patients/{id}/sessions` e
  `dashboard/overview`.

Da decidere: mantenerli con un uso dichiarato, o rimuoverli.

---

## 4. Incoerenze con la documentazione

| documento | stato |
|---|---|
| `docs/android.md:17` sulla copertura LiDAR | **risolto** — il testo ora dice "copertura assente dal riepilogo (non 0%)" |
| `docs/server.md` sul limite dei dispositivi | **risolto** — `:41` documenta l'isolamento per dispositivo, i codici 409/404 e `ingest.denied`; `:43` i limiti di frequenza; `:51-53` la semantica del proprietario |
| `docs/architettura.md` non cita `FingerTapAnalyzer` né il conteggio sit-to-stand | **aperto** — verifica con `grep`: zero occorrenze |
| `docs/architettura.md:153` dichiara "38 test" | **aperto** — i casi sono 122 |
| `Sync/SyncContracts.cs:8-9` "condiviso dai due lati per costruzione" | **aperto** — il commento è invariato, ma il server continua a non registrare `SyncJsonContext` e si affida al camelCase di default |

Un dettaglio strutturale non documentato, ancora valido: su iOS il sink riceve dati da **due** canali,
le `ISensorSource` e `ArCaptureController.WriteFrameStreams`, che scrive direttamente luce, riepilogo
LiDAR e blendshape. Su Android quel secondo canale non esiste.

---

## 5. Cosa è solido

Vale la pena dirlo, perché sono scelte non ovvie. Le voci sono quelle della prima stesura, più quelle
nate con i fix.

- **Privacy verso il modello linguistico**: al LLM va solo `SessionSummary`, senza dati grezzi né
  identificativi; le note libere sono delimitate con `<<< >>>` contro la prompt injection.
- **Cifratura dell'archivio**: SQLCipher 4 sull'intero file, chiave a 256 bit nel Portachiavi o avvolta
  dal Keystore, e un file con chiave sbagliata **non viene mai sovrascritto**.
- **Confronto della chiave di dispositivo a tempo costante** con SHA-256, e verifica della password
  eseguita anche quando l'utente non esiste, contro l'enumerazione degli utenti.
- **Scritture atomiche** dell'audio e della chiave su Android (file temporaneo + rename), e validazione
  dell'id come 32 esadecimali contro il path traversal.
- **Idempotenza dell'ingest**: `ON CONFLICT DO NOTHING` sulla chiave composita
  `(SessionId, Kind, Sequence)`, sicura anche con invii concorrenti.
- **Il limite applicato mentre si scrive** (`AudioStorage`), non dopo: un file fuori misura non riempie
  prima il disco.
- **Un registro di audit consultabile** e completo su login, login fallito, logout, ingestione,
  ingestione negata, lettura, cancellazione e riproduzione audio.
- **La traduzione in stile gettext** (`Core/Localization/Loc.cs`): la chiave è il testo italiano scritto
  nel codice, così il codice resta leggibile, l'italiano non richiede un file e una traduzione mancante
  ricade sull'italiano invece di mostrare una sigla.
- **Il commento che spiega perché**: le divergenze tra iOS e Android e le scelte controintuitive sono
  motivate nel codice dove servono.

---

## 6. Cosa non è stato verificato

- Nessun test è stato eseguito e nessuna prova a runtime è stata fatta: tutte le voci sono statiche.
- Le voci marcate **dedotto** (§1.4 e il caso dell'alias Keystore) non sono state provate.
- Le voci **dall'indice** derivano da ricognizione con lettura parziale: da confermare prima di agire.
- **Le aree nate dopo la prima stesura non sono state revisionate**: `Core/Localization`,
  `Core/Bluetooth`, `Core/Telemetry`, `Server/Endpoints/FhirEndpoints`, `JwtTokenService`,
  `SystemEndpoints`, `AuditEndpoints`, `AudioStorageHealthCheck`, e la dashboard aggiornata. Sono
  elencate qui sopra per quello che si è visto dell'architettura, non per una revisione di merito.
- Il bridge Swift, il bridge Java e la dashboard React non sono stati analizzati con la stessa
  profondità del codice C#.
