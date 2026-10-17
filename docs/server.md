# Server clinico e dashboard 3D

Backend **ASP.NET Core 11** (`src/ClinicalSensorFusion.Server`) con **PostgreSQL**, e dashboard
**React + Three.js** (`src/ClinicalSensorFusion.Dashboard`, React Three Fiber) servita dallo stesso server.

## 1. Cosa fa

| Area | Funzioni |
|---|---|
| Anagrafica pazienti | Codice pseudonimo (lo stesso inserito nell'app), nome, cognome, data di nascita, sesso, codice fiscale, contatti, indirizzo, diagnosi, note. Ricerca, paginazione, modifica con **concorrenza ottimistica**, cancellazione definitiva (diritto all'oblio, anche dei file audio). |
| Ricezione dall'app | Metadati della rilevazione, riepilogo clinico, **tutti i flussi dei sensori** (blocchi compressi identici a quelli del telefono), file **CAF**, analisi IA. Invio idempotente: si può ripetere senza duplicati. Un codice sconosciuto crea un'anagrafica "da completare". |
| Osservabilità | Log strutturati, metriche e tracce (vedi [telemetria.md](telemetria.md)); pagina **Stato del sistema** con controlli di salute, traffico/errori/latenza per rotta, ingestione e accessi. |
| Dashboard | Panoramica (KPI, attività settimanale 3D, **galassia dei tremori** 3D, ROM medio 3D, rilevazioni recenti); paziente (**paesaggio 3D dell'escursione articolare** nel tempo, trend, anagrafica); rilevazione (**replay 3D dello scheletro** LiDAR, **spettrogramma 3D** del tremore, indicatori, audio, analisi IA). |

## 2. Architettura

```mermaid
flowchart LR
  APP[App iOS<br/>clinical.db cifrato] -- "X-Api-Key<br/>PUT/POST /api/v1/ingest" --> API
  subgraph Server ASP.NET Core 11
    API[Minimal API] --> EF[(EF Core)]
    API --> FS[(Archivio audio<br/>fuori da wwwroot)]
    SPA[wwwroot: React + Three.js]
  end
  EF --> PG[(PostgreSQL)]
  BROWSER[Browser clinico] -- "cookie HttpOnly<br/>/api/v1/*" --> API
  BROWSER --> SPA
```

- **Contratto condiviso**: i DTO di sincronizzazione (`Core/Sync`) e il codec dei blocchi sono nel Core, usati
  sia dall'app sia dal server. Il server decodifica i blocchi con lo stesso codice che li ha scritti.
- **Dati pesanti lato server**: replay e spettrogramma sono calcolati e sottocampionati sul server
  (≤ 900 fotogrammi, ≤ 160 colonne); al browser arrivano poche centinaia di KB.
- **Indicatori denormalizzati** (durata, tremore, copertura LiDAR) per le query della panoramica; il riepilogo
  completo è in `jsonb`.

## 3. Sicurezza

| Rischio | Contromisura |
|---|---|
| Furto della chiave del dispositivo | Solo l'hash SHA-256 è in configurazione; la chiave consente **solo** di inviare dati, non di leggerli, e **solo sulle rilevazioni di quel dispositivo**: metadati, blocchi e audio di una rilevazione creata da un altro dispositivo sono rifiutati (409/404) e il tentativo va nel registro (`ingest.denied`). Revoca = rimuovere la voce. |
| Blocchi dei sensori malevoli | Ogni blocco è verificato all'arrivo (decodificabile, numero di campioni coerente) e la decompressione ha un tetto di 8 MB per blocco, anche in lettura: una "bomba" Brotli è rifiutata con 400 invece di esaurire la memoria quando si apre la rilevazione. |
| Abuso / saturazione | Limiti di frequenza su **tutte** le rotte (sezione `RateLimits`): 600 richieste/minuto per IP, 300/minuto per dispositivo in ingestione, 2 caricamenti audio contemporanei per dispositivo, 60/minuto per le sonde anonime `/health` e `/health/ready` (che aprono il database e scrivono sull'archivio audio); `/health/live` non esegue controlli e resta esente, perché è la sonda degli orchestratori. Risposta `429` con `Retry-After`. Lotti di blocchi fino a 16 MB; audio fino a `AudioStorage:MaxFileBytes`, applicato durante la scrittura. |
| Credenziali della dashboard | Password PBKDF2 (ASP.NET Core Identity v3); verifica a tempo costante anche per utenti inesistenti; **rate limit** 5 login/minuto per IP. |
| Accesso alle API senza browser | `POST /api/v1/auth/token` emette un **JWT** (HMAC-SHA256, 30 minuti) con le stesse credenziali, lo stesso limite di tentativi e lo stesso audit del login. Dà gli stessi permessi del cookie, non quelli dei dispositivi. `Jwt:SigningKey` (≥ 32 byte) va impostata fuori dal repository; senza, la chiave è casuale e i token scadono al riavvio. |
| Furto di sessione / CSRF | Cookie `HttpOnly`, `Secure`, `SameSite=Strict`, scadenza 8 h; SPA sulla stessa origine, nessun token in `localStorage`. |
| XSS / clickjacking | CSP `default-src 'self'` (niente CDN), `frame-ancestors 'none'`, `nosniff`, `no-referrer`. |
| Path traversal sui file | Id validati (32 hex) prima di comporre i percorsi; file scritti in modo atomico e verificati come CAF. |
| Tracciabilità | `audit_log` per login riusciti e **falliti**, logout, letture e modifiche dell'anagrafica, letture delle rilevazioni, riproduzione audio, ingestione (anche rifiutata), con indirizzo IP e user-agent. Consultabile da `GET /api/v1/audit` (anche la consultazione è registrata). |

Il proprietario di una rilevazione è il **nome** del dispositivo in `Ingest:Devices`: rinominare una voce equivale
a cambiare dispositivo (per sostituire una chiave compromessa basta cambiare `KeySha256` lasciando il nome). Le
rilevazioni ricevute prima di questo vincolo non hanno proprietario e vengono adottate dal primo dispositivo che le reinvia.

Da fare prima della produzione: TLS sul reverse proxy (es. Cloudflare Tunnel), backup cifrati di PostgreSQL e
dell'archivio audio, cifratura del volume dati, DPIA (GDPR art. 35) per dati sanitari.

## 4. Avvio in sviluppo

```bash
docker compose up -d                                   # PostgreSQL 16 locale, senza password (solo 127.0.0.1)
cd src/ClinicalSensorFusion.Dashboard && npm ci && npm run build && cd -   # dashboard in wwwroot
dotnet run --project src/ClinicalSensorFusion.Server --launch-profile http
```

Con un PostgreSQL già installato che richiede una password, impostarla fuori dal repository:
`dotnet user-secrets set ConnectionStrings:Clinical "Host=localhost;Database=clinical_dev;Username=…;Password=…" --project src/ClinicalSensorFusion.Server`.
Il repository non contiene credenziali del database (anche CI e docker-compose usano l'autenticazione `trust` su istanze effimere/locali).

Apri http://localhost:5180 — utente `demo`, password `clinica2026`. In sviluppo (`appsettings.Development.json`)
il database `clinical_dev` viene popolato con **6 pazienti fittizi** e ~30 rilevazioni sintetiche
(cinematica plausibile di squat, tremori a 4,8 / 7,5 / 9,5 Hz). Per sviluppare la dashboard con hot reload:
`npm run dev` (Vite su http://localhost:5173, inoltra `/api` al server).

Chiave di sviluppo per l'app: `dev-device-key-solo-sviluppo`.

### Con .NET Aspire

In alternativa ai passi qui sopra, un solo comando avvia tutto con la dashboard di Aspire (log, metriche e
tracce di ogni servizio, stato delle risorse). Serve Docker (o Podman) per il container di PostgreSQL.

```bash
# Una volta sola: gli hash delle credenziali, negli user-secrets dell'AppHost (mai nel repository)
dotnet run --project src/ClinicalSensorFusion.Server --no-launch-profile -- hash-password '<password>'
dotnet run --project src/ClinicalSensorFusion.Server --no-launch-profile -- new-device-key iphone-aspire
dotnet user-secrets set Parameters:dashboard-password-hash '<hash della password>' --project src/ClinicalSensorFusion.AppHost
dotnet user-secrets set Parameters:device-key-sha256 '<KeySha256>' --project src/ClinicalSensorFusion.AppHost

dotnet run --project src/ClinicalSensorFusion.AppHost   # dashboard di Aspire: http://localhost:15180
```

L'AppHost (`src/ClinicalSensorFusion.AppHost`) orchestra tre risorse:

| Risorsa | Cosa | Indirizzo |
|---|---|---|
| `postgres` / `clinical` | PostgreSQL in container, database `clinical`, dati nel volume `clinical-sensor-fusion-pgdata`. La porta sull'host la sceglie Aspire, quindi convive con un PostgreSQL già installato o con docker compose sulla 5432; la password è generata e salvata negli user-secrets dell'AppHost. | indicato nella dashboard di Aspire |
| `server` | Il server, profilo `aspire` (ambiente Development, dati demo). Riceve `ConnectionStrings__Clinical` dal riferimento al database e parte quando PostgreSQL è pronto; Aspire lo considera sano quando risponde `/health/ready`. | http://localhost:5181 |
| `dashboard` | Vite con hot reload (`npm run dev`, dipendenze installate da Aspire); inoltra `/api`, `/health` e `/fhir` al server tramite `SERVER_URL`. | indicato nella dashboard di Aspire |

Credenziali: utente `demo` (parametro `dashboard-user`) con la password di cui si è salvato l'hash; nell'app la
chiave generata, con il nome del parametro `device-name` (predefinito `iphone-aspire`). I parametri sostituiscono
la prima voce di `Dashboard:Users` e di `Ingest:Devices`, quindi sotto Aspire la password `clinica2026` e la
chiave di sviluppo non valgono. Se un parametro manca, la dashboard di Aspire lo chiede all'avvio; in
alternativa vanno bene le variabili `Parameters__<nome>`.

Il server usa `ClinicalSensorFusion.ServiceDefaults` (`builder.AddServiceDefaults()` e
`app.MapDefaultEndpoints()`): strumentazione OpenTelemetry di ASP.NET Core, client HTTP e runtime, esportazione
OTLP quando c'è `OTEL_EXPORTER_OTLP_ENDPOINT` (Aspire lo imposta verso la propria dashboard), service discovery e
resilienza dei client HTTP. Il server aggiunge sopra le proprie metriche e tracce (`csf.*`); le sonde
`/health`, `/health/ready` e `/health/live` restano quelle del server, con i loro limiti di frequenza, e
`MapDefaultEndpoints` non ne aggiunge altre.

Cosa resta uguale: l'avvio classico qui sopra, `scripts/run-server-lan.sh` (porta 5180, credenziali in
`entrata.log`), docker compose, i test e la produzione non dipendono da Aspire. Senza
`OTEL_EXPORTER_OTLP_ENDPOINT` la telemetria resta nel processo come prima, e `Telemetry:OtlpEndpoint` continua a
funzionare. Le versioni dei pacchetti Aspire sono in `Directory.Packages.props`, quella dell'SDK dell'AppHost in
`global.json`: vanno aggiornate insieme.

### Dal telefono, sulla rete locale

Il profilo di sviluppo ascolta solo su `localhost` e usa credenziali note (quelle qui sopra). Per provare
l'invio da un telefono sulla stessa Wi-Fi:

```bash
./scripts/run-server-lan.sh        # http://<indirizzo del computer>:5180
```

Al primo avvio lo script genera una password per la dashboard e una chiave del dispositivo: le credenziali di
sviluppo non valgono più. Il server riceve solo gli hash, da `.server-lan.env`; credenziali in chiaro e hash
finiscono in `entrata.log`, che lo script mostra a ogni avvio insieme all'indirizzo da inserire nell'app e a cui
aggiunge una riga con data e indirizzo. Entrambi i file sono locali, leggibili solo dal proprio utente ed esclusi
da git: `entrata.log` contiene segreti in chiaro e non va condiviso. Nell'app: Impostazioni → *Server clinico* →
`https://<indirizzo>:5180`, la chiave e l'impronta del certificato. Per rigenerarle basta cancellare `.server-lan.env` (o `entrata.log`). Se
sul computer gira un PostgreSQL senza l'utente `postgres` di docker compose, lo script usa l'utente di sistema;
altrimenti la stringa di connessione si può cambiare in `.server-lan.env`.

Il traffico è cifrato anche in rete locale: lo script genera un certificato autofirmato in `.server-lan/`
(escluso da git; la chiave resta la stessa anche se cambia l'indirizzo) e avvia il server solo in HTTPS. Un
certificato autofirmato non è riconosciuto dal telefono, quindi l'app lo accetta solo se coincide l'**impronta**
SHA-256 della chiave pubblica che lo script mostra a ogni avvio (Impostazioni → *Server clinico* → *Impronta del
certificato*): chi si mettesse in mezzo con un altro certificato verrebbe rifiutato. Il browser della dashboard
mostra un avviso la prima volta. Per un uso reale vale la sezione seguente, con un certificato di un'autorità
(lì l'impronta si lascia vuota).

Ollama parla solo HTTP: il telefono non lo chiama più direttamente. Il server inoltra a Ollama (`Ai:OllamaUrl`,
di default `http://localhost:11434/`) le sole due chiamate che l'app usa, `GET /api/v1/ai/api/tags` e
`POST /api/v1/ai/api/chat`, con la chiave del dispositivo. Nell'app: Impostazioni → *Server Ollama* → *IA
tramite il server clinico*. Un Ollama remoto resta utilizzabile se è dietro HTTPS (per esempio Cloudflare Access).

### Da Internet, con un tunnel Cloudflare

Il server della rete locale si può raggiungere anche da fuori con un tunnel Cloudflare con nome, senza aprire
porte sul router. Sulla macchina di sviluppo il tunnel `clinicalfusion` risponde a `https://clica.syntaxnode.work`:

- configurazione in `~/.cloudflared/clinicalfusion.yml` (fuori dal repository): l'ingresso inoltra a
  `https://localhost:5180` verificando il certificato autofirmato con la sua copia (`caPool:
  .server-lan/server.crt`, `originServerName: localhost`), quindi il traffico è cifrato anche tra
  `cloudflared` e il server;
- avvio permanente con launchd (`~/Library/LaunchAgents/work.syntaxnode.clinicalfusion-tunnel.plist`): parte al
  login e si riavvia se cade; log in `~/Library/Logs/clinicalfusion/tunnel.log`;
- il server dietro resta quello di `run-server-lan.sh`: se è spento il tunnel risponde 502.

Nell'app, da fuori casa: *Server clinico* → `https://clica.syntaxnode.work` e impronta **vuota** (il certificato
è di un'autorità pubblica); *IA tramite il server clinico* compila `https://clica.syntaxnode.work/api/v1/ai/`.
Dati e Ollama restano protetti dalla chiave del dispositivo, la dashboard dal login.

Comandi utili:

```bash
launchctl kickstart -k gui/$(id -u)/work.syntaxnode.clinicalfusion-tunnel   # riavvio
launchctl bootout gui/$(id -u)/work.syntaxnode.clinicalfusion-tunnel        # stop definitivo
```

### Accesso con Google (single sign-on) e registrazione con approvazione

Oltre a utente e password, la dashboard accetta l'accesso con un account Google, che serve anche a
**registrarsi**. Google conferma chi è la persona (serve un indirizzo **verificato** da Google); poi:

- **utenti della configurazione** (`Dashboard:Users`) con `GoogleEmail`: entrano subito. Sono gli
  **amministratori**, come chi entra con utente e password;
- **primo accesso di chiunque altro**: si registra una *richiesta di accesso* (tabella `dashboard_accounts`) e la
  pagina di login lo dice. Finché la richiesta è in attesa la persona non vede nessun dato;
- un amministratore decide dalla pagina **Accessi** della dashboard (`/accounts`): *Approva*, *Approva come
  amministratore*, *Rifiuta*; per un account già approvato, *Togli l'accesso*. Una decisione vale subito anche per
  le sessioni già aperte, perché lo stato dell'account si ricontrolla a ogni richiesta;
- account approvato: entra con Google, con la stessa sessione del login con password (cookie di 8 ore).

Ogni passaggio finisce nel registro di audit (`account.request`, `account.approved`, `account.rejected`,
`account.disabled`, `auth.login` con `google`, `auth.login.failed` con l'indirizzo) e gli accessi nella metrica
`csf.auth.logins` con `method=google`. Un utente della configurazione può avere solo `GoogleEmail`, senza
`PasswordHash`: per lui il login con password non riesce mai. API per gli amministratori: `GET /api/v1/accounts/`,
`POST /api/v1/accounts/{id}/approve` (corpo facoltativo `{"isAdmin": true}`), `/reject`, `/disable`.

Configurazione:

1. Google Cloud Console → *API e servizi* → *Credenziali* → *Crea credenziali* → *ID client OAuth* → tipo
   *Applicazione web*. URI di reindirizzamento autorizzati: `https://clica.syntaxnode.work/signin-google` (e, per
   provare dal Mac, `https://localhost:5180/signin-google`). Google non accetta indirizzi IP della rete locale:
   dal telefono o da un altro computer l'accesso con Google passa dal tunnel.
2. Nel file `.server-lan.env` togliere il commento alle righe `Authentication__Google__ClientId`,
   `Authentication__Google__ClientSecret` e `Dashboard__Users__0__GoogleEmail` (il tuo indirizzo Google, così sei
   amministratore anche con Google) e compilarle (il segreto non va nel repository), poi riavviare
   `run-server-lan.sh`.
3. Nella pagina di login compare **Accedi o registrati con Google** (la dashboard chiede a `GET /api/v1/auth/providers` se è
   attivo). Senza configurazione il pulsante non c'è e `GET /api/v1/auth/google` risponde 404.

### Lingua della dashboard e delle risposte del server

La dashboard è in italiano e in inglese: la lingua si sceglie dal menu in fondo alla barra laterale (o nella
pagina di accesso), vale subito e resta salvata nel browser; alla prima visita segue la lingua del browser.
I testi stanno nel codice in italiano e le traduzioni in `Dashboard/src/i18n/en.ts`; un test fallisce se ne
manca una.

Il server risponde nella lingua dell'intestazione `Accept-Language` (la dashboard manda quella scelta):
messaggi d'errore e di validazione, rapporto della rilevazione, descrizioni delle API FHIR e controlli di
salute. Senza intestazione resta l'italiano. Le traduzioni sono nel catalogo condiviso con l'app
(`Core/Localization/en.json`). Restano in italiano i dati (nomi salvati, testi delle risorse FHIR), la
documentazione OpenAPI e i messaggi di log.

## 5. Produzione

```bash
# Hash della password di un utente della dashboard
dotnet run --project src/ClinicalSensorFusion.Server --no-launch-profile -- hash-password '<password>'
# Nuova chiave per un iPhone (la chiave va inserita nell'app, l'hash nel server)
dotnet run --project src/ClinicalSensorFusion.Server --no-launch-profile -- new-device-key iphone-ambulatorio-1
dotnet publish src/ClinicalSensorFusion.Server -c Release -o out   # compila anche la dashboard (npm)
```

Configurazione (variabili d'ambiente o secret store, mai nel repository):

```
ConnectionStrings__Clinical=Host=…;Database=clinical;Username=…;Password=…
Dashboard__Users__0__UserName=…            Dashboard__Users__0__PasswordHash=…
Ingest__Devices__0__Name=iphone-1           Ingest__Devices__0__KeySha256=…
AudioStorage__Root=/var/lib/clinical/audio
Jwt__SigningKey=<almeno 32 byte casuali>    # senza: chiave temporanea, token validi fino al riavvio
# Facoltativi (valori predefiniti indicati)
ApiDocs__Enabled=false                      # true: espone /swagger e /openapi/v1.json anche fuori dallo sviluppo
Telemetry__OtlpEndpoint=                    Telemetry__LogDirectory=          # vedi docs/telemetria.md
RateLimits__GlobalPerMinute=600             RateLimits__LoginPerMinute=5
RateLimits__IngestPerMinute=300             RateLimits__ConcurrentAudioUploads=2
RateLimits__HealthPerMinute=60              # sonde anonime; /health/live resta esente
Fhir__MaxSessionsScanned=2000               # oltre: 413 too-costly (vedi docs/fhir.md §4)
Fhir__MaxSessionsPerExport=5000             # oltre: 413 too-costly su $everything
```

Nell'app iOS: Impostazioni → *Server clinico* → indirizzo HTTPS e chiave del dispositivo.

## 6. API

OpenAPI su `/openapi/v1.json`, **Swagger UI su `/swagger`** e Scalar su `/scalar`: attivi in sviluppo, altrove
solo con `ApiDocs:Enabled=true`. In Swagger: eseguire `POST /api/v1/auth/token`, copiare `access_token` in
«Authorize» (schema *Bearer*); le rotte di ingestione usano invece lo schema *ApiKey* (`X-Api-Key`). Principali rotte:

| Metodo | Rotta | Autorizzazione |
|---|---|---|
| `PUT` | `/api/v1/ingest/sessions/{id}` | dispositivo |
| `POST` | `/api/v1/ingest/sessions/{id}/chunks` | dispositivo |
| `PUT` | `/api/v1/ingest/sessions/{id}/audio/{audioId}` | dispositivo |
| `POST` | `/api/v1/auth/login` · `logout` · `GET me` | — / clinico |
| `POST` | `/api/v1/auth/token` | — (restituisce il JWT) |
| `GET/POST/PUT/DELETE` | `/api/v1/patients[/{id}]` | clinico |
| `GET` | `/api/v1/patients/{id}/sessions` · `/trends` | clinico |
| `GET` | `/api/v1/sessions/{id}` · `/pose` · `/spectrogram` · `/streams/{kind}` · `/audio/{audioId}.wav` | clinico |
| `GET` | `/api/v1/dashboard/overview` | clinico |
| `GET` | `/api/v1/audit?actor=&action=&entityId=&from=&to=&page=&pageSize=` | clinico |
| `GET` | `/api/v1/system/health` · `/system/metrics` | clinico |
| `GET` | `/fhir/metadata` · `/fhir/Patient` · `/fhir/Observation` · `/fhir/DiagnosticReport` · `/fhir/Patient/{id}/$everything` | — (metadata) / clinico: HL7 FHIR R4, vedi [fhir.md](fhir.md) |
| `GET` | `/health` · `/health/ready` · `/health/live` | — (anonime: le prime due con limite dedicato per IP, `/health/live` esente) |

## 7. Verifiche

- **Server: 61 test di integrazione** su PostgreSQL reale. Coprono:
  - autenticazione e autorizzazioni, compreso l'isolamento tra dispositivi;
  - interfaccia HL7 FHIR R4, con ogni risposta riletta dal modello R4 di riferimento;
  - stato del servizio, metriche, token JWT (anche contraffatti) e documentazione Swagger;
  - rifiuto dei blocchi non decodificabili e degli audio fuori misura, limiti di frequenza, registro di audit;
  - header di sicurezza;
  - anagrafica CRUD, validazione e concorrenza ottimistica;
  - percorso completo **DB cifrato del telefono → sincronizzatore → API → PostgreSQL → dashboard**, incluso il reinvio idempotente;
  - conversione CAF→WAV, path traversal e dati demo.
- **Core: 102 test.** La sincronizzazione end-to-end ha fatto emergere un errore reale nel calcolo del tremore (il
  modulo dell'accelerazione raddoppiava la frequenza) e la necessità di una soglia di significatività. Entrambi corretti.
- **Dashboard:** typecheck TypeScript strict, test Vitest, build Vite e verifica visiva con Chromium headless
  (WebGL software) di tutte le pagine, senza errori in console.
- **Non verificato qui:** esecuzione su .NET 11 (l'ambiente aveva l'SDK 10; la CI usa .NET 11).
