# Criticità e verifiche

Revisione tecnica del repository: problemi di sicurezza, robustezza e incoerenze tra codice e
documentazione. Non è un documento di conformità e non sostituisce una verifica a runtime.

Indice costruito con `codegraph` (`.codegraph/codegraph.db`, 175 file, 2.999 simboli) e lettura
diretta del sorgente. Nessuna riga di codice è stata modificata.

## Come leggere le voci

Ogni voce porta un livello di verifica:

- **verificato** — ho letto il codice e la riga citata dice quello che scrivo.
- **dall'indice** — ricavato dall'indice o da una ricognizione, con lettura parziale: da confermare prima di agire.
- **dedotto** — ragionamento sul comportamento atteso, non verificato.

Le posizioni `file:riga` sono riferite alla radice del repository.

### Un limite dello strumento, da tenere presente

`codegraph callers` **non vede le chiamate ai metodi statici**: risponde "No callers found" per
`ClinicalPromptBuilder`, che invece è chiamato in `src/ClinicalSensorFusion.App/ViewModels/SessionDetailViewModel.cs:165`
e `:313` (verificato con `grep`). Un `callers` vuoto non è una prova di assenza: va confermato.

---

## 1. Sicurezza

### 1.1 Nessuno scoping per dispositivo nell'ingest — gravità alta · verificato

Qualunque chiave di dispositivo valida può fare upsert di **qualunque** identificativo di sessione e
sostituirne l'audio, compresi quelli di altri dispositivi.

- `src/ClinicalSensorFusion.Server/Endpoints/IngestEndpoints.cs:18` — il gruppo `/ingest` applica solo
  `RequireAuthorization(Policies.Ingest)`. L'identità del dispositivo non viene mai letta: nessun uso
  di `User.Identity` o `FindFirst` in `IngestEndpoints.cs` né in `IngestService.cs` (verificato con `grep`).
- `src/ClinicalSensorFusion.Server/Services/IngestService.cs:30-34` — la sessione è cercata **solo** per
  `s.Id == upload.Id`. Non esiste una colonna che leghi la sessione al dispositivo che l'ha creata.

Impatto: con una chiave compromessa si possono sovrascrivere sessioni altrui, alterarne il contenuto e
creare pazienti arbitrari. `docs/server.md:40` dice "solo inviare dati, non leggerli" ma non dice
"solo i propri": il limite documentato non è quello che serve.

Direzione: registrare sull'entità sessione il `Name` del dispositivo al primo upsert e rifiutare (403)
gli upsert successivi da un dispositivo diverso; oppure introdurre un `DeviceId` e filtrare su quello.

### 1.2 Decompressione senza limite sui blocchi dei sensori — gravità alta · verificato

`SensorChunkCodec.Decode` decomprime il payload Brotli in un `MemoryStream` **senza alcun tetto di
dimensione** (`src/ClinicalSensorFusion.Core/Recording/SensorChunkCodec.cs:64-67`):

```csharp
using var input = new BrotliStream(new MemoryStream(payload), CompressionMode.Decompress);
using var buffer = new MemoryStream();
input.CopyTo(buffer);          // nessun limite: un payload piccolo può espandere quanto vuole
```

Il ciclo di parsing che segue non controlla i limiti: legge `n` (fino a 65535) e alloca
`new float[n]` per ogni campione senza verificare che i byte restanti bastino.

Il server, dal canto suo, **memorizza i payload grezzi senza decodificarli**
(`src/ClinicalSensorFusion.Server/Services/IngestService.cs:113-117`, `INSERT … ON CONFLICT DO NOTHING`).
La decodifica avviene più tardi, sul percorso di lettura del clinico:

- `src/ClinicalSensorFusion.Server/Services/SessionAnalytics.cs:38` — quando si apre `/pose`,
  `/spectrogram` o `/streams/{kind}`.

Impatto: un dispositivo malevolo (o una chiave rubata) può caricare payload che esplodono in memoria
quando un clinico apre la sessione, oppure causare un'eccezione non gestita al posto di un 400.

Direzione: limitare la dimensione decompressa (confronto progressivo durante `CopyTo`, o un tetto
derivato da `SampleCount × valori massimi per campione`), validare che i byte restanti bastino prima di
ogni lettura, e restituire `InvalidDataException` trattata come 400.

### 1.3 Rate limiting solo sul login — gravità media · dall'indice

`src/ClinicalSensorFusion.Server/Program.cs:79-86` definisce una sola policy (`login`, 5/minuto per IP).
Non c'è un limiter globale, quindi restano senza limite gli endpoint di ingest (fino a **320 MB** per
PUT audio, limite dichiarato in `IngestEndpoints.cs:23-25`) e quelli della dashboard.

Impatto: esaurimento di disco e memoria, e creazione illimitata di pazienti segnaposto
(vedi 1.5).

### 1.4 `X-Forwarded-For` non validato — gravità media · dedotto

`src/ClinicalSensorFusion.Server/Program.cs:88-94` abilita i forwarded headers con `KnownProxies` e
`KnownIPNetworks` **svuotati**. Senza proxy noti il middleware non valida la catena, quindi l'header è
falsificabile da chiunque (dedotto dal comportamento documentato del middleware, non provato qui).

Impatto: il rate limit per IP del login diventa aggirabile variando l'header; schema e cookie possono
essere influenzati.

### 1.5 Autocreate dei pazienti non validata — gravità media · dall'indice

`IngestService.UpsertSessionAsync` crea un `Patient` segnaposto quando il codice soggetto è nuovo
(`IngestService.cs:21-28`), ma `IngestEndpoints` valida solo lunghezza e non-vuoto
(`IngestEndpoints.cs:38-41`). La validazione più stretta esiste altrove e non è applicata qui:
`PatientEndpoints.PatientInput.Validate` rifiuta i caratteri di controllo (`PatientEndpoints.cs:203`).

Impatto: un dispositivo può popolare l'anagrafica con codici arbitrari, che poi restano.

### 1.6 Registro di audit cieco — gravità media · dall'indice

- Nessun endpoint legge `AuditLog`: la tabella è scritta e mai consultata (`AuditService.cs:9-16`).
- I login **falliti** non sono tracciati (`AuthEndpoints.cs:31-34`) e il logout nemmeno (`:50-54`).
- `AuditEntry` non contiene IP, user-agent né valori prima/dopo (`Data/Entities.cs:169-180`).
- `AuditService.Record` aggiunge la voce allo **stesso** `DbContext`: la riga esiste solo se il
  chiamante fa `SaveChanges`.

Nel complesso il registro non è utilizzabile per un'indagine a posteriori.

### 1.7 Scritture su richieste di lettura — gravità bassa · dall'indice

`session.read` (`SessionEndpoints.cs:132-133`), `patient.read` (`PatientEndpoints.cs:83-84`) e
`audio.play` (`SessionEndpoints.cs:183-184`) eseguono `SaveChangesAsync`. Una GET che scrive non è
idempotente e apre una scrittura su un percorso pensato in sola lettura.

### 1.8 Altro

- `/health` è `AllowAnonymous` (`Program.cs:130`) ed espone lo stato del database.
- L'app accetta lo schema `http` per il server di sincronizzazione
  (`src/ClinicalSensorFusion.App/Services/SyncService.cs:95`) e su Android abilita esplicitamente il
  traffico in chiaro verso la rete locale (`MauiProgram.cs:90-98`): la chiave del dispositivo può
  viaggiare in chiaro se configurata male.
- La revoca di un dispositivo consiste nel rimuovere la voce dalla configurazione
  (`Security/SecurityOptions.cs:10-15`, nessun flag di scadenza): richiede modifica e ricarica, senza
  rotazione.

---

## 2. Robustezza e correttezza

### 2.1 L'audio mancante non blocca la sincronizzazione — gravità media · dall'indice

`SessionSynchronizer.cs:132-136` registra un warning se il file audio non esiste e **prosegue**, poi
marca comunque la sessione come sincronizzata (`:143`). I metadati audio restano sul server senza il
file, e il reinvio automatico non ritenta il file: serve un reinvio manuale.

### 2.2 Uno stream del modello troncato viene salvato come risposta valida — gravità media · dall'indice

`OllamaClient.ChatStreamAsync` termina il ciclo quando il flusso finisce, senza verificare di aver
ricevuto `done: true` (`src/ClinicalSensorFusion.Core/Ai/OllamaClient.cs:41-73`). Il testo parziale
viene persistito come `AiAnalysis` completa.

### 2.3 Timeout di rete indistinguibile dall'annullamento — gravità media · dall'indice

Un timeout dell'`HttpClient` (10 minuti, `MauiProgram.cs:78`) arriva come `OperationCanceledException` e
finisce nel ramo che scrive `"[interrotta]"` (`SessionDetailViewModel.cs:342-345`), lo stesso
dell'annullamento volontario dell'operatore.

### 2.4 Migrazioni non coperte — gravità media · dall'indice

Nessun test riferisce `Migrations` in `tests/ClinicalSensorFusion.Core.Tests/PersistenceTests.cs`. La
guardia contro un database più recente del supportato esiste (`Persistence/Migrations.cs:88-92`), ma
non è verificata da nessun test. Lato server esiste una sola migrazione iniziale.

### 2.5 Copertura LiDAR = 0 su Android invece di "non applicabile" — gravità bassa · verificato

`SessionMetricsAggregator.cs:206` calcola `_lidarPoseFrames / _poseFrames`, e su Android `_lidarPoseFrames`
resta a zero perché si incrementa solo quando il marcatore di sorgente vale `0` (`:113`), mentre Android
scrive sempre `1` (`Platforms/Android/Vision/VisionPipeline.cs:197`). Con pose presenti il valore
risultante è quindi **`0.0`**, non `null`.

`docs/android.md:17` dichiara questo campo "non disponibile (NaN)": la distanza del soggetto lo è
davvero, la copertura no.

Impatto contenuto, e verificato: `Domain/MeasurementReliability.cs:68` richiede `coverage > 0`, quindi
non penalizza il punteggio. Resta un valore che si legge come "0% di copertura LiDAR", cioè un difetto
di qualità del dato, invece di "non applicabile". Nessun test copre il caso.

### 2.6 Altro

- **Gate di sincronizzazione asimmetrico**: `SyncNowAsync` non attende (`SyncService.cs:30-33`) mentre
  `SyncSessionAsync` attende il turno (`:54`). Un invio singolo può restare accodato dietro una
  sincronizzazione completa.
- **`AudioStorage` controlla il limite di dimensione dopo aver scritto tutto** il corpo su disco
  (`Services/AudioStorage.cs:58-66`); il limite che anticipa davvero è quello della richiesta.
- **`/dashboard/overview` carica in memoria tutte le sessioni della finestra** e deserializza il
  `SummaryJson` di ognuna, senza paginazione (`Endpoints/DashboardEndpoints.cs:45-71`).
- **Modello predefinito sospetto**: `SettingsDefaults.OllamaModel = "gemma4:e4b-mlx"`
  (`Core/Settings/AppSettings.cs:26`). Non corrisponde a una famiglia Ollama nota; se il modello non
  esiste, la lista lo sostituisce col primo disponibile ma la configurazione salvata resta quella.
- **Chiave del database in una stringa managed**: `PRAGMA key = "x'{hex}'"` (`Persistence/ClinicalDatabase.cs:103`)
  costruisce una `string` non azzerabile che resta nell'heap fino alla raccolta. `Array.Clear(key)`
  avviene solo sul percorso di successo, non nei `catch` (`:105`).
- **Chiave errata riconosciuta solo dal codice 26**: `ClinicalDatabase.cs:118` intercetta solo
  `SQLITE_NOTADB`; altre varianti emergerebbero come `SqliteException` grezza.
- **iOS, chiave esistente di lunghezza diversa da 32** (`Platforms/iOS/Security/KeychainKeyProvider.cs:30-50`):
  non è gestita esplicitamente e porta a un errore di duplicato invece che diagnostico.
- **Android, alias Keystore orfano** (`Platforms/Android/Security/KeystoreKeyProvider.cs:59-68`): se il
  file avvolto è perso ma l'alias resta, la rigenerazione può fallire. *dedotto*, non provato.

---

## 3. Codice esposto e mai usato

- `GET /api/v1/sessions/{id}/streams/{kind}` e il tipo `StreamSeries`: la dashboard non li chiama
  (nessun hook o chiamata a `streams/` in `src/ClinicalSensorFusion.Dashboard/src`), e `StreamSeries`
  non compare in `api/types.ts`.
- `GET /api/v1/sessions` (lista globale): la dashboard passa solo da `patients/{id}/sessions` e
  `dashboard/overview`.

Da decidere: mantenerli per uso futuro dichiarato, o rimuoverli.

---

## 4. Incoerenze con la documentazione

| documento | dice | codice |
|---|---|---|
| `docs/android.md:17` | copertura LiDAR non disponibile (NaN) | `0.0` — §2.5, verificato |
| `docs/server.md:40` | "solo inviare dati, non leggerli" | manca il vincolo "solo i propri" — §1.1 |
| `docs/architettura.md:31` | `Core/Signal/` = FFT, tremore, indicatori audio | contiene anche `FingerTapAnalyzer` |
| `docs/architettura.md` | non cita | il conteggio sit-to-stand, i flussi `PoseLandmarks2D/3D`, `HandLandmarks`, `FaceBlendshapes`, `LidarDepth`, e il ricalcolo degli angoli lato server (`SessionAnalytics.cs:44`) |
| `src/ClinicalSensorFusion.Core/Sync/SyncContracts.cs:8-9` | contratto condiviso per costruzione | il server non registra `SyncJsonContext`: si affida al camelCase di default (`Program.cs:39-44`), quindi una futura opzione del source generator non verrebbe replicata automaticamente |

Un dettaglio strutturale non documentato: su iOS il sink riceve dati da **due** canali, le `ISensorSource`
e `ArCaptureController.WriteFrameStreams` (`Platforms/iOS/Vision/ArCaptureController.cs:181`), che scrive
direttamente luce, riepilogo LiDAR e blendshape. Su Android quel secondo canale non esiste.

---

## 5. Cosa è invece solido

Vale la pena dirlo, perché sono scelte non ovvie e fatte bene:

- **Privacy verso il modello linguistico**: al LLM va solo `SessionSummary`, senza dati grezzi né
  identificativi (`Core/Domain/SessionSummary.cs:7-9`); le note libere sono delimitate con `<<< >>>`
  contro la prompt injection (`Core/Ai/ClinicalPromptBuilder.cs:63-68`).
- **Cifratura dell'archivio**: SQLCipher 4 sull'intero file, chiave a 256 bit nel Portachiavi o avvolta
  dal Keystore, e un file con chiave sbagliata **non viene mai sovrascritto**
  (`Persistence/ClinicalDatabase.cs:138-146`).
- **Confronto della chiave di dispositivo a tempo costante** con SHA-256
  (`Security/ApiKeyAuthenticationHandler.cs:34-53`), e verifica della password eseguita anche quando
  l'utente non esiste, contro l'enumerazione degli utenti (`Security/DashboardUserService.cs:13-18`).
- **Scritture atomiche** dell'audio e della chiave su Android (file temporaneo + rename), validazione
  dell'id come 32 esadecimali contro il path traversal (`Services/AudioStorage.cs:28-31`).
- **Idempotenza dell'ingest**: `ON CONFLICT DO NOTHING` sulla chiave composita `(SessionId, Kind, Sequence)`,
  sicura anche con invii concorrenti (`IngestService.cs:113-117`).
- **Il commento che spiega perché**: le divergenze tra iOS e Android sono motivate nel codice dove
  servono, per esempio sui cookie della WebView e sul traffico in chiaro (`MauiProgram.cs:87-98`).

---

## 6. Cosa non è stato verificato

- Nessun test è stato eseguito e nessuna prova a runtime è stata fatta: tutte le voci sono statiche.
- Le voci marcate **dedotto** (§1.4, parte di §2.6) non sono state provate.
- Le voci **dall'indice** derivano da una ricognizione con lettura parziale del sorgente: vanno
  confermate riga per riga prima di un intervento.
- Non sono stati analizzati il bridge Swift, il bridge Java né la dashboard React con la stessa
  profondità del codice C#.
