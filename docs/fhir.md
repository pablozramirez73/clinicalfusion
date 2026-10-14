# Interoperabilità HL7 FHIR

Il server espone i dati clinici con lo standard **HL7 FHIR R4 (4.0.1)** su `/fhir`: un altro sistema sanitario
(cartella clinica, fascicolo, software di reparto) può leggerli e importarli senza conoscere le API proprie
dell'applicazione.

## 1. Cosa significa "compatibile HL7" qui

HL7 è una famiglia di standard. Questo server implementa **FHIR R4 in sola lettura**, cioè:

| Requisito della specifica FHIR | Stato |
|---|---|
| `GET /fhir/metadata` restituisce un **CapabilityStatement** (obbligatorio per ogni server) | sì, pubblico |
| Risorse in `application/fhir+json` | sì (solo JSON; `_format=xml` → 406) |
| Interazione `read`: `GET /fhir/{Tipo}/{id}` | Patient, Observation, DiagnosticReport |
| Interazione `search-type`: risultato come **Bundle** `searchset` con `total`, `link` e paginazione | sì: `total` è esatto (`_count` predefinito 50, massimo 200; la paginazione si segue con `link.next`) |
| Errori come **OperationOutcome** | sì (400, 404, 405, 406) |
| Terminologie standard | LOINC per segni vitali e passi, UCUM per le unità |
| Profili | segni vitali di FHIR (`heartrate`, `oxygensat`, `bodytemp`) |
| Operazione `Patient/{id}/$everything` | sì: è l'esportazione completa di un paziente, non paginata (vedi §4) |
| `ETag` e `Last-Modified` in lettura | sì per Patient |

**Non** è implementato, e va saputo prima di collegare un altro sistema:

- **scrittura** (create, update, delete): l'interfaccia è in sola lettura, le scritture ricevono 405;
- **SMART on FHIR / OAuth 2.0**: l'accesso usa il token JWT dell'applicazione (vedi sotto), non un server di
  autorizzazione standard;
- **HL7 v2** (messaggi ADT/ORU) e **CDA**: sono standard diversi da FHIR;
- **XML**, `_include`/`_revinclude`, cronologia delle versioni, esportazione massiva (`$export`);
- profili nazionali (es. FSE 2.0): servirebbero codifiche e profili specifici.

Le risorse prodotte sono verificate nei test rileggendole con il modello R4 di riferimento (Firely .NET SDK) in
modalità rigorosa. Non è una certificazione: il server non è stato sottoposto al validatore ufficiale HL7 né a
test di interoperabilità con altri sistemi.

## 2. Accesso

Stesse credenziali e stessi permessi dei clinici; i dispositivi (chiave `X-Api-Key`) non possono leggere.

```bash
TOKEN=$(curl -s -X POST https://<server>/api/v1/auth/token -H 'Content-Type: application/json' \
  -d '{"userName":"…","password":"…"}' | jq -r .access_token)

curl -H "Authorization: Bearer $TOKEN" 'https://<server>/fhir/Patient?identifier=PZ-001'
curl -H "Authorization: Bearer $TOKEN" 'https://<server>/fhir/Patient/<id>/$everything' > paziente.fhir.json
```

Dalla dashboard: pagina del paziente → **Esporta FHIR**. Ogni lettura ed esportazione è nel registro di audit
(`fhir.patient.read`, `fhir.observation.search`, `fhir.export`, …).

## 3. Risorse

| Risorsa | Da cosa nasce | Ricerca |
|---|---|---|
| `Patient` | anagrafica; `identifier` = codice pseudonimo (`urn:clinical-sensor-fusion:patient-code`) e codice fiscale (`urn:oid:2.16.840.1.113883.2.9.4.3.2`) | `_id`, `identifier`, `name`, `family`, `birthdate` |
| `DiagnosticReport` | una rilevazione; `result` punta alle sue osservazioni | `patient`, `date` |
| `Observation` | un indicatore di una rilevazione; id `{rilevazione}-{indicatore}` | `patient`, `code`, `category`, `date` |

| Indicatore | Codice | Unità (UCUM) | Categoria |
|---|---|---|---|
| Frequenza cardiaca (sensore Bluetooth) | LOINC `8867-4` | `/min` | vital-signs |
| Saturazione (saturimetro Bluetooth) | LOINC `2708-6` + `59408-5` | `%` | vital-signs |
| Temperatura (termometro Bluetooth) | LOINC `8310-5` | `Cel` | vital-signs |
| Passi (pedometro) | LOINC `55423-8` | `{steps}` | activity |
| Escursione articolare per articolazione | locale `rom-…`, con componenti minimo, massimo, media | `deg` | exam |
| Frequenza dominante del tremore | locale `tremor-frequency`, componente ampiezza RMS | `Hz`, `m/s2` | exam |
| Alzate dalla sedia | locale `sit-to-stand-count` | — | exam |

I codici "locali" appartengono al sistema `urn:clinical-sensor-fusion:indicator`: per questi indicatori non è
stato adottato un codice LOINC o SNOMED CT perché non ne è stato verificato uno corrispondente, e un codice
standard sbagliato sarebbe peggio di uno locale dichiarato. Chi importa i dati può mapparli.

Per i segni vitali il valore è la **media** sulla rilevazione; minimo, massimo e numero di misure sono nella nota.
`date` filtra sull'inizio della rilevazione, con i prefissi `ge`, `gt`, `le`, `lt`, `eq`.

Non vengono esportati: note libere dell'operatore, testo delle analisi del modello linguistico, audio, flussi
grezzi dei sensori e coordinate GPS.

## 4. Limiti di costo

Le risorse si generano al momento dai dati archiviati, e per una parte dei filtri questo significa leggere il
riepilogo di ogni rilevazione. Dove il conteggio esatto diventerebbe troppo oneroso il server **rifiuta**, invece
di restituire un campione presentato come l'insieme completo: mai dati incompleti spacciati per interi.

| Caso | Comportamento |
|---|---|
| `Observation?code=…` o `?category=…` su più di `Fhir:MaxSessionsScanned` rilevazioni (predefinito 2000) | `413` con OperationOutcome `too-costly`: restringere con `patient` o con `date` |
| `Patient/{id}/$everything` su più di `Fhir:MaxSessionsPerExport` rilevazioni (predefinito 5000) | `413` con OperationOutcome `too-costly`: usare la ricerca FHIR per parti |

`code` e `category` non sono colonne ma proprietà del riepilogo, quindi il `total` veritiero impone di leggere
tutte le rilevazioni che soddisfano il filtro SQL: è il tetto a rendere il costo prevedibile. Le ricerche senza
quei filtri (`Patient`, `DiagnosticReport`, e `Observation` filtrata solo per `patient`/`date`) restano
disponibili entro i tetti. Entrambi i valori si alzano dalla configurazione (sezione `Fhir`).
