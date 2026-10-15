# Modello del rischio di caduta

Stima la probabilità che una persona over 65 sia caduta nell'ultimo anno dalle caratteristiche del movimento
nei test dell'app: TUG, 5 alzate dalla sedia (5TSTS), tandem e cammino (10MWT).

Il modello **non è ancora nell'app**. Prima va tarato e validato sulle nostre rilevazioni (sotto), e un
modello che stima un rischio clinico va inquadrato secondo l'MDR prima di mostrarlo agli operatori.

## Come funziona

1. **Pre-addestramento** su GSTRIDE (163 anziani con e senza cadute, test clinici e passo misurato con un
   IMU sul piede). Il primo tentativo su [KINECAL](https://physionet.org/content/kinecal/1.0.3/) (scheletri
   Kinect degli stessi test) non ha dato un modello utile: risultati sotto.
2. **Taratura e validazione** sulle rilevazioni dell'app, con le cadute riferite dai pazienti.

Le caratteristiche ([`features.py`](fall_risk/features.py)) sono calcolate allo stesso modo dallo scheletro
del Kinect e dai 33 punti di MediaPipe, e sono solo misure interne al corpo. Su Android i punti 3D hanno
l'origine al centro delle anche, quindi lo spostamento nella stanza non c'è e la velocità di cammino non
si può usare. Le misure sono: altezza del bacino rispetto alle caviglie (alzate e sedute), inclinazione del
tronco, ritmo e regolarità dei passi, oscillazione del bacino nel tandem.

Il modello è una regressione logistica: un peso per caratteristica, leggibile e trasportabile nell'app come
una manciata di numeri (file JSON in `models/`).

## Risultati del pre-addestramento (15 ottobre 2026)

### GSTRIDE: il modello da usare

[GSTRIDE](https://zenodo.org/records/8003441) (Zenodo, CC BY 4.0): 163 anziani tra 70 e 98 anni, 86 caduti
nell'ultimo anno. Si usano solo le misure che l'app ricava anch'essa ([`gstride_train.py`](fall_risk/gstride_train.py)):

| Misure | AUC fuori campione (IC 95%) |
|---|---|
| Solo tempo del TUG | 0,78 (0,71–0,85) |
| TUG, velocità del cammino, punteggi SPPB di alzata ed equilibrio | 0,80 (0,73–0,87) |
| Le stesse + cadenza, durata e variabilità del passo → **`models/fall_risk_gstride.json`** | **0,81 (0,74–0,87)** |
| Riferimento: anche età, questionari, forza, lunghezza del passo (l'app non li ha) | 0,84 (0,77–0,90) |

Validazione incrociata stratificata a 5 parti ripetuta 50 volte, penalità scelta dentro ogni parte.

Limiti da tenere presenti:
- La popolazione è geriatrica (età media 83 anni, anche residenze per anziani): la separazione riflette in
  parte la fragilità. Su pazienti più giovani o meno compromessi l'AUC sarà più bassa.
- Le misure dell'app non sono identiche: velocità sul 10MWT invece che sui 4 m; punteggio SPPB
  dell'equilibrio stimato dal solo tandem; passi dalla distanza tra le caviglie invece che da un IMU sul
  piede (le conversioni sono in [`app_export.py`](fall_risk/app_export.py)).
- Le probabilità del modello pre-addestrato sono troppo vicine al 50% (forte penalità): la ricalibrazione
  sui nostri dati non è facoltativa.

### KINECAL: scartato

**Il modello pre-addestrato su KINECAL non distingue chi è caduto: non va usato.** Su 57 over 65 di
KINECAL (24 con cadute), validazione incrociata ripetuta 50 volte:

| Modello | AUC fuori campione (IC 95%) |
|---|---|
| Tutte le 24 caratteristiche | 0,54 (0,39–0,69) |
| 5 caratteristiche scelte a priori | 0,42 (0,27–0,58) |
| Tempo del TUG da solo, come caratteristica | AUC 0,55 |
| Tempo stimato delle 5 alzate, da solo | AUC 0,56 |

Le caratteristiche contengono informazione: separano gli over 65 dagli adulti più giovani con AUC 0,82
(0,72–0,90). Il limite è l'etichetta: «almeno una caduta in 12 mesi», scivolate comprese, su poche persone.
Anche le misure cliniche classiche (TUG, 5 alzate) qui non la predicono.

## Preparazione

```bash
cd ml
uv venv --python 3.13 .venv
uv pip install --python .venv/bin/python -r requirements.txt
```

## 1. Pre-addestramento

```bash
.venv/bin/python -m fall_risk.kinecal_download   # ~350 MB di scheletri dal bucket pubblico di PhysioNet
.venv/bin/python -m fall_risk.train              # → models/fall_risk_kinecal.json (scartato, vedi sopra)
```

## 2. Taratura e validazione sulle nostre rilevazioni

Servono, per ogni paziente, le rilevazioni dei test inviate al server e la risposta alla domanda di KINECAL:
*«Negli ultimi 12 mesi è caduto, anche scivolando o inciampando, finendo a terra o su un piano più basso?»*

```bash
CSF_USER=... CSF_PASSWORD=... .venv/bin/python -m fall_risk.app_export --server http://localhost:5180
# data/app/labels.csv: patient_code,faller   (1 = almeno una caduta negli ultimi 12 mesi)
.venv/bin/python -m fall_risk.calibrate --model models/fall_risk_gstride.json \
    --features data/app/features.csv --labels data/app/labels.csv
```

Il registro di GSTRIDE si prepara così (lo zip è di 1,4 GB, serve solo il CSV):

```bash
mkdir -p data/gstride && curl -L -o data/gstride/GSTRIDE_database.zip \
    https://zenodo.org/api/records/8003441/files/GSTRIDE_database.zip/content
unzip -j data/gstride/GSTRIDE_database.zip GSTRIDE_database/Database_register.csv -d data/gstride
.venv/bin/python -m fall_risk.gstride_train   # → models/fall_risk_gstride.json
```

La taratura confronta, sempre fuori campione: il modello così com'è, il modello con le caratteristiche
riportate alla scala dei dati dell'app, e lo stesso con le probabilità ricalibrate. Con almeno 25 pazienti
per gruppo ristima anche i pesi, trattenendoli vicino a quelli pre-addestrati. Sotto i 10 pazienti per
gruppo si ferma senza scrivere un modello.

I dati scaricati (`data/`) restano in locale e sono esclusi da git.
