# Clinical Sensor Fusion — funzionalità ricostruite

Ricostruzione fatta l'11/10/2026 leggendo dall'iPhone 12 Pro i metadati dell'app e il suo contenitore dati.
L'eseguibile non è leggibile da un iPhone non jailbroken, quindi questo documento **non è completo**: descrive solo ciò che l'app ha lasciato sul telefono.

Legenda: **[certo]** = letto direttamente dai file; **[dedotto]** = inferenza ragionevole, da confermare.

## 1. Identità dell'app

| Voce | Valore |
|---|---|
| Nome visualizzato | Clinical Sensor Fusion |
| Identificativo | `com.lucaguelfi.clinicalsensorfusion` |
| Versione | 1.0 (build 1) |
| Eseguibile | `ClinicalSensorFusion.app/ClinicalSensorFusion` |
| Team di firma | `Y7S9A29635` (profilo gratuito, vecchio account) |
| Primo avvio sul telefono | 29/09/2026 |
| Ultimo utilizzo | 11/10/2026 |

## 2. Tecnologia

- **[certo]** .NET su iOS con runtime **CoreCLR** (`libcoreclr`), non Mono.
- **[certo]** Codice applicativo precompilato in **ReadyToRun** (`ClinicalSensorFusion.r2r.framework`).
- **[certo]** Build di **Debug**: sono inclusi i componenti del debugger (`libmscordbi`, `libmscordaccore`) e in `tmp/` ci sono decine di pipe di debug, una coppia per ogni avvio dal 29/09 all'11/10.
- **[certo]** Nessun framework nativo di terze parti caricato all'avvio: solo le librerie native di .NET (Globalization, Compression, Net.Security, Cryptography.Apple).
- **[certo]** L'app usa una **WebView** (dati WebKit, cache GPU WebKit, cookie).
- **[certo]** L'app usa **Metal** direttamente (cache shader propria di circa 5 MB).
- **[dedotto]** .NET MAUI 11, come indicato dall'autore; coerente con quanto sopra.

## 3. Funzionalità accertate

### 3.1 Registrazione audio
- **[certo]** L'app registra audio e lo salva in `Library/audio/<id>/audio.caf`, una cartella per registrazione con identificativo casuale di 32 caratteri.
- **[certo]** Formato: CAF, **mono, 48 000 Hz, Float32** non compresso (circa 11,5 MB al minuto).
- **[certo]** Sul telefono ci sono **33 registrazioni**, da 1,1 MB a 37,8 MB, cioè da circa 6 secondi a circa 3 minuti e 20 secondi.
- **[dedotto]** Il formato non compresso ad alta fedeltà fa pensare a un segnale da analizzare (un "sensore" acustico), non a semplici note vocali.

### 3.2 Archivio clinico locale cifrato
- **[certo]** I dati sono in `Library/clinical.db`, 54,5 MB.
- **[certo]** Il file è **interamente cifrato**: nessuna intestazione SQLite, entropia 8,0 bit/byte.
- **[dedotto]** SQLite con SQLCipher, chiave custodita nel Portachiavi iOS.
- **[certo]** La cartella `Documents` è vuota: l'app non salva esportazioni visibili nell'app File.

### 3.3 Analisi con modello linguistico (Ollama)
- **[certo]** Impostazioni salvate: `ollama.server`, `ollama.model`, `ollama.language`.
- **[certo]** Valori attuali: server sulla rete locale alla porta 11434, modello `gemma4:e4b-mlx`, lingua della risposta `Italiano`.
- **[certo]** L'app chiama `GET /api/tags` sul server, cioè legge l'elenco dei modelli disponibili (test di connessione o selettore del modello).
- **[certo]** Ci sono cookie di sessione per `ollama.com` e per un host protetto da Cloudflare Access (`ai.syntaxnode.work`).
- **[dedotto]** Il server Ollama era raggiungibile anche da remoto dietro Cloudflare Access, con accesso fatto dentro la WebView dell'app.

### 3.4 Identificazione dell'operatore
- **[certo]** Impostazioni salvate: `operator.id` e `operator.name`.
- **[dedotto]** Ogni rilevazione viene associata all'operatore che l'ha eseguita.

### 3.5 Inserimento di testo
- **[certo]** iOS ha creato modelli di tastiera per italiano e inglese: l'app ha campi di testo libero usati in entrambe le lingue.

## 4. Cosa non è recuperabile dal telefono

- Elenco e aspetto delle schermate.
- Quali sensori vengono "fusi" oltre al microfono (fotocamera, LiDAR, movimento, Bluetooth).
- Struttura del database (tabelle, campi).
- Prompt inviati al modello e formato delle risposte.
- Eventuali esportazioni, sincronizzazioni o report.

## 5. Come completare questo documento

1. Aprire l'app finché si avvia ancora e fare uno screenshot di ogni schermata, incluse Impostazioni e una rilevazione completa.
2. Passare gli screenshot al Mac (AirDrop).
3. Integrare qui le sezioni mancanti a partire dagli screenshot.
