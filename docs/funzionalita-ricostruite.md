# Clinical Sensor Fusion — funzionalità ricostruite

Ricostruzione fatta l'11/10/2026 da queste fonti:

- i metadati dell'app e il suo contenitore dati, letti dall'iPhone 12 Pro;
- nove screenshot: le quattro schede a riposo, la scheda Acquire fino in fondo e durante una registrazione, le impostazioni Ollama in Devices;
- il prompt di sistema completo, fornito dall'autore: vedi [prompt-di-sistema.md](prompt-di-sistema.md);
- le indicazioni dell'autore sulla tecnologia usata.

L'eseguibile non è leggibile da un iPhone non jailbroken, quindi questo documento **non è completo**: vedi la sezione 6 per ciò che manca.

Legenda: **[certo]** = letto dai file o visibile negli screenshot; **[autore]** = indicato dall'autore; **[dedotto]** = inferenza ragionevole, da confermare.

## 1. Identità dell'app

| Voce | Valore |
|---|---|
| Nome visualizzato | Clinical Sensor Fusion |
| Identificativo | `com.lucaguelfi.clinicalsensorfusion` |
| Versione | 1.0 (build 1) |
| Eseguibile | `ClinicalSensorFusion.app/ClinicalSensorFusion` |
| Team di firma | `Y7S9A29635` (profilo gratuito, vecchio account) |
| Primo avvio sul telefono | 29/09/2026 |
| Lingua dell'interfaccia | Inglese |
| Destinatari | **[autore]** Professionisti sanitari, soprattutto fisioterapisti |

## 2. Tecnologia

- **[certo]** .NET su iOS con runtime **CoreCLR** (`libcoreclr`), non Mono.
- **[certo]** Codice applicativo precompilato in **ReadyToRun** (`ClinicalSensorFusion.r2r.framework`).
- **[certo]** Build di **Debug**: sono inclusi i componenti del debugger (`libmscordbi`, `libmscordaccore`).
- **[certo]** Nessun framework nativo di terze parti caricato all'avvio: solo le librerie native di .NET.
- **[certo]** L'app usa una **WebView** (dati WebKit, cookie) e **Metal** (cache shader propria di circa 5 MB).
- **[autore]** Interfaccia in **.NET MAUI** su **.NET 11**.
- **[autore]** Analisi visiva con la libreria **MediaPipe**.
- **[certo]** MediaPipe è usata per la stima della **posa** e gira su **GPU** (stato mostrato dall'app: "pose GPU").
- **[dedotto]** MediaPipe non compare tra le librerie native caricate all'avvio: o è caricata più tardi, o gira nella WebView (versione web, con accelerazione GPU).
- **[certo]** Al cambio di scheda iOS segnala blocchi dell'interfaccia da 1 a 3 secondi (indicatore di sistema visibile negli screenshot): il caricamento delle schede Devices, Sessions e Audit avviene sul thread principale.

## 3. Struttura dell'interfaccia

- **[certo]** Tema scuro, accento verde acqua, schede con angoli arrotondati.
- **[certo]** Barra inferiore con quattro schede: **Acquire**, **Devices**, **Sessions**, **Audit**.
- **[certo]** Ogni scheda ha un titolo grande e un sottotitolo descrittivo.

## 4. Funzionalità per scheda

### 4.1 Accesso dell'operatore

- **[certo]** L'operatore si identifica con codice e nome; l'intestazione di Acquire mostra `nome · Face ID`.
- **[certo]** Pulsante di uscita in alto a destra nella scheda Acquire.
- **[certo]** Ogni accesso viene scritto nel registro di audit ("Operator signed in").
- **[dedotto]** Lo sblocco avviene con Face ID e viene richiesto a ogni apertura dell'app.

### 4.2 Acquire — acquisizione

- **[certo]** Riquadro di anteprima video; a riposo mostra "Camera starts with the acquisition": la fotocamera si accende solo quando parte l'acquisizione.
- **[certo]** Selettore della sorgente: **Back · LiDAR** (fotocamera posteriore con profondità LiDAR) oppure **Front · TrueDepth** (fotocamera anteriore con profondità TrueDepth).
- **[certo]** Pulsante in alto a destra nel riquadro, con icona di messa a fuoco/inquadratura.
- **[certo]** Campo **Patient ID (wristband)** con accanto un pulsante di lettura senza contatto.
- **[dedotto]** Il pulsante legge il braccialetto del paziente via NFC.
- **[certo]** Sezione **Exam** con scelta del tipo di esame, a scorrimento orizzontale.
- **[autore]** Gli esami sono nove:

| Esame | Cosa valuta | Visto negli screenshot |
|---|---|---|
| Finger tapping | Battito delle dita: velocità e regolarità del movimento | sì |
| Tremor assessment | Tremore: frequenza e ampiezza | sì |
| Romberg test | Equilibrio in stazione eretta | sì |
| Timed Up and Go | Alzarsi, camminare, girarsi e risedersi: mobilità funzionale | no |
| 10-meter walk | Velocità del cammino su 10 metri | no |
| 30-second chair stand | Alzate dalla sedia in 30 secondi: forza degli arti inferiori | no |
| Gait analysis | Analisi del cammino | no (citato nel prompt) |
| Range of motion | Ampiezza di movimento articolare | no |
| Free acquisition | Acquisizione libera, senza protocollo | sì (elenco sessioni) |

- **[dedotto]** I nomi inglesi degli esami non visti sono quelli standard; l'etichetta esatta nell'app è da confermare.
- **[certo]** Sotto l'esame scelto compare una riga di descrizione; per uno degli esami: "All selected sensors until the operator stops", cioè tutti i sensori selezionati fino allo stop manuale.
- **[dedotto]** Gli altri esami hanno una durata o un insieme di sensori predefiniti.
- **[certo]** Durante l'acquisizione viene registrato anche l'audio: mono, 48 000 Hz, Float32, salvato in `Library/audio/<id>/audio.caf`.

**Durante la registrazione**
- **[certo]** Sull'anteprima video compaiono tre indicatori: **REC** con il tempo trascorso (rosso), i **fotogrammi al secondo** (25 fps nello screenshot) e **depth** quando la profondità è attiva.
- **[certo]** Sul video è disegnato lo **scheletro della posa**: punti arancioni e segmenti verdi.
- **[certo]** Mentre si registra, il campo paziente e la scelta dell'esame sono disattivati.
- **[certo]** Più in basso c'è un pulsante circolare rosso con la scritta "Recording · mm:ss".
- **[dedotto]** Lo stesso pulsante avvia e ferma l'acquisizione.

**Elenco dei sensori attivi**
- **[certo]** Durante la registrazione una lista con spunta verde mostra ogni sorgente e il suo stato:

| Sorgente | Dettaglio mostrato |
|---|---|
| IMU (accel · gyro · mag · attitude) | 100 Hz · riferimento `XMagneticNorthZVertical` |
| Barometer / altimeter | — |
| Activity recognition | — |
| Pedometer | — |
| Camera + MediaPipe + depth | Back LiDAR Depth Camera · 640x480 · depth 320x240 Float32 · pose GPU |
| Microphone + audio classifier | 48000 Hz · iPhone Microphone |
| BLE medical devices | "No device connected (Devices tab)" |
| GPS / location | — |

- **[certo]** L'audio non viene solo registrato: passa anche per un **classificatore audio**.
- **[dedotto]** Il classificatore è quello di sistema (Sound Analysis).
- **[certo]** La "fusione" combina quindi movimento del telefono, barometro, attività e passi, video con posa e profondità, audio, dispositivi Bluetooth e posizione.

**Opzioni di acquisizione** (parte bassa della scheda)
- **[certo]** Due interruttori a pillola: **Depth** e **GPU**.
- **[dedotto]** Depth attiva la cattura della profondità; GPU sceglie l'esecuzione di MediaPipe su GPU invece che su CPU.
- **[certo]** **Motion sampling**: frequenza di campionamento dei sensori di movimento a scelta tra **100 Hz**, **50 Hz** e **25 Hz**.

**Riquadri Live**
- **[certo]** Dieci riquadri con valore in tempo reale e indicatore di stato: pallino grigio e trattino a riposo, pallino colorato e valore durante la registrazione.

| Riquadro | Sorgente |
|---|---|
| Motion | **[certo]** accelerazione in g, componenti x/y/z, campo magnetico in µT con livello di accuratezza, grafico nel tempo |
| Rotation | **[certo]** velocità angolare in rad/s, roll/pitch/yaw in gradi, grafico nel tempo |
| Vision | **[dedotto]** risultati di MediaPipe sul video (valore numerico, non leggibile per intero) |
| Audio | **[certo]** microfono (valore numerico, non leggibile per intero) |
| Posture & anthropometry | **[dedotto]** punti del corpo da MediaPipe più profondità, per postura e misure corporee |
| Vitals | **[dedotto]** frequenza cardiaca, SpO2 e pressione arteriosa dai dispositivi Bluetooth |
| Pressure | **[certo]** barometro / altimetro del telefono |
| Activity | **[dedotto]** riconoscimento dell'attività e contapassi |
| Location | **[dedotto]** posizione GPS |
| Synchronisation | **[dedotto]** allineamento temporale tra i flussi dei sensori |

- **[certo]** Synchronisation è l'ultimo riquadro: la scheda Acquire finisce lì.

### 4.3 Devices — periferiche, dati sanitari e servizi

Sottotitolo: "Medical peripherals, health data and services".

**Bluetooth medical devices**
- **[certo]** Profili GATT supportati: **Heart Rate**, **Pulse Oximeter**, **Blood Pressure**.
- **[certo]** Le fasce toraciche ECG con intervalli RR sono supportate attraverso il profilo Heart Rate.
- **[certo]** Pulsante **Scan for medical devices**, elenco dei dispositivi trovati ("No devices yet.") e riga delle ultime letture ("No readings yet").

**Apple Health**
- **[certo]** Pulsante **Authorise and read Health** e stato del caricamento ("Not loaded"): l'app chiede il permesso e legge dati da HealthKit.

**AI report · Ollama**
- **[certo]** Modello linguistico **self-hosted**: "Only de-identified metrics are sent", cioè al modello arrivano solo metriche de-identificate.
- **[certo]** "Plain HTTP only on the local network": HTTP in chiaro è ammesso solo sulla rete locale.
- **[certo]** Campi del modulo: **indirizzo del server**, **chiave** (opzionale, vuota nello screenshot), pulsante **Connect**, menu del **modello** (ogni voce indica se è locale), menu della **lingua del referto**, pulsante **Save**.
- **[certo]** Sotto il modulo, riga di stato "Using <modello>".
- **[certo]** Impostazioni salvate: `ollama.server`, `ollama.model`, `ollama.language`; modello in uso `gemma4:e4b-mlx`, lingua del referto `Italiano`.
- **[certo]** L'app chiama `GET /api/tags` sul server per leggere i modelli disponibili.
- **[certo]** Sono presenti cookie di sessione per `ollama.com` e per un host protetto da Cloudflare Access (`ai.syntaxnode.work`).
- **[dedotto]** L'accesso al server remoto avviene dentro la WebView dell'app.

**AI system prompt**
- **[certo]** Sezione con il **prompt di sistema** del modello, mostrato in carattere a spaziatura fissa. Il testo completo è in [prompt-di-sistema.md](prompt-di-sistema.md).
- **[dedotto]** Il prompt è modificabile dall'operatore.

**Cosa rivela il prompt sul funzionamento dell'app**
- **[autore]** L'app invia al modello un **payload JSON in formato HL7 FHIR R4/R5**, con risorse `Observation` e `DiagnosticReport` che portano metadati della sessione, timestamp e tipo di esame.
- **[autore]** Il payload contiene **landmark 3D**: X e Y in coordinate normalizzate, Z in metri grazie al LiDAR.
- **[autore]** I landmark coprono tre modelli MediaPipe: **Pose** (articolazioni), **Hand** (dita) e **Face** (volto).
- **[autore]** Il payload contiene le **serie temporali inerziali** grezze di accelerometro e giroscopio (60–100 Hz).
- **[autore]** Tra i tipi di esame è citata anche la **Gait Analysis** (analisi del cammino).
- **[autore]** Il modello deve calcolare:
  - **cinematica e goniometria**: ampiezza di movimento (ROM) in gradi dal prodotto scalare tra segmenti corporei, e asimmetrie destra/sinistra;
  - **tremore**: frequenza dominante in Hz e ampiezza, con classificazione a riposo, posturale o cinetico (4–6 Hz parkinsoniano, 8–12 Hz fisiologico);
  - **stabilometria**: oscillazione antero-posteriore dall'asse Z e area di oscillazione (sway area) del baricentro stimato da anche e spalle.
- **[autore]** Il referto è in Markdown con struttura fissa: tabella **Riepilogo Sessione**, **Insight Biomeccanici**, **Metriche Neurologiche e Stabilità**, **Clinical Flags**.
- **[autore]** Regole: nessun calcolo su dati assenti ("Non Valutabile"), angoli arrotondati al grado e distanze al millimetro, riga finale che dichiara l'output come supporto alle decisioni cliniche (SaMD/CDSS) e non come diagnosi.
- **[dedotto]** I calcoli elencati sono affidati al modello linguistico: dal prompt non risulta che l'app li esegua da sé prima dell'invio.

### 4.4 Sessions — archivio delle sessioni

Sottotitolo: "46 sessions · encrypted on this device".

- **[certo]** Elenco delle sessioni raggruppate per giorno ("Today", poi data estesa).
- **[certo]** Ogni riga mostra: nome o codice del paziente, ora, durata, operatore, tipo di esame con icona e colore propri.
- **[certo]** Etichetta **AI report** sulle sessioni che hanno già un referto generato.
- **[certo]** Ogni riga apre una schermata di dettaglio; l'apertura è registrata nell'audit ("Session viewed").
- **[certo]** I dati sono in `Library/clinical.db` (54,5 MB), file interamente cifrato.
- **[dedotto]** SQLite con SQLCipher, chiave nel Portachiavi iOS.
- **[certo]** Le sessioni sono 46 ma le registrazioni audio sul telefono sono 33: non tutte le sessioni hanno un file audio.

### 4.5 Audit — registro delle attività

Sottotitolo: "Append-only · tamper-evident".

- **[certo]** Registro in sola aggiunta, con ogni evento legato al precedente da un hash **SHA-256** (catena di hash).
- **[certo]** Pulsante **Verify hash chain** che ricalcola ogni anello della catena; stato iniziale "Integrity not checked".
- **[certo]** Elenco **Latest events**; ogni evento mostra data e ora, codice operatore, soggetto (operatore o paziente), numero progressivo e inizio dell'hash.
- **[certo]** Tipi di evento visti: **Operator signed in**, **Session viewed**, **AI report requested**, **AI report received**.
- **[dedotto]** Esistono altri tipi di evento (creazione sessione, uscita, collegamento dispositivo) non visibili nello screenshot.

## 5. Dati salvati sul dispositivo

| Percorso | Contenuto |
|---|---|
| `Library/clinical.db` | Archivio cifrato: sessioni, referti, audit |
| `Library/audio/<id>/audio.caf` | Una registrazione audio per sessione |
| `Library/Preferences/…plist` | `ollama.server`, `ollama.model`, `ollama.language`, `operator.id`, `operator.name` |
| `Documents/` | Vuota: nessuna esportazione visibile nell'app File |

## 6. Cosa manca ancora

- Per ogni esame: descrizione mostrata, durata e sensori predefiniti.
- Come è integrata la libreria MediaPipe (nativa o nella WebView).
- La schermata di **dettaglio di una sessione**: metriche calcolate, grafici, testo del referto.
- La schermata di **accesso** dell'operatore.
- Le metriche calcolate per ogni esame (per esempio oscillazione nel Romberg, frequenza e ampiezza nel finger tapping).
- La struttura del database e la forma esatta del payload FHIR inviato al modello.
- Eventuali funzioni di esportazione o condivisione.
