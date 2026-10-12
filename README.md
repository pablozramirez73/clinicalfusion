# clinicalfusion

Ricostruzione di **Clinical Sensor Fusion**, app iOS in .NET MAUI (.NET 11) il cui sorgente originale è andato perso.
Esiste anche una [versione Android](docs/android.md), senza LiDAR né TrueDepth.

L'app acquisisce una rilevazione clinica fondendo **MediaPipe** (Pose, Hands, Face) con **tutti i sensori
dell'iPhone** — LiDAR, fotocamere posteriore e TrueDepth, IMU a 100 Hz, barometro, pedometro, microfono,
prossimità, luce, GPS opzionale — calcola indicatori (escursione articolare 3D, tremore, cammino, voce),
li salva in un archivio locale cifrato e li fa commentare a un modello linguistico su un server **Ollama**.

I dati vengono poi inviati a un **server ASP.NET Core 11** con PostgreSQL, che gestisce l'**anagrafica dei
pazienti** e offre una **dashboard 3D** (React + Three.js): replay dello scheletro LiDAR, spettrogramma del
tremore, paesaggio dell'escursione articolare, galassia dei tremori.

## Documentazione

- [docs/funzionalita-ricostruite.md](docs/funzionalita-ricostruite.md): cosa è stato ricavato dall'app installata.
- [docs/architettura.md](docs/architettura.md): app iOS — architettura, sensori, fusione, sicurezza, build, migrazione.
- [docs/android.md](docs/android.md): versione Android — differenze rispetto a iOS, sicurezza, build, cosa è verificato.
- [docs/server.md](docs/server.md): server, anagrafica, dashboard 3D, sicurezza, avvio e produzione.

## Avvio rapido (Mac con Xcode 26 e .NET 11)

```bash
brew install cocoapods xcodegen
dotnet workload install maui-ios
./scripts/download-models.sh
dotnet build src/ClinicalSensorFusion.App -f net11.0-ios -t:Run
```

Android (JDK 21 e Android SDK, qualsiasi OS): `dotnet workload install maui-android`, poi
`dotnet build src/ClinicalSensorFusion.App -f net11.0-android -t:Run`.

Server e dashboard (qualsiasi OS, serve PostgreSQL: `docker compose up -d`):

```bash
cd src/ClinicalSensorFusion.Dashboard && npm ci && npm run build && cd -
dotnet run --project src/ClinicalSensorFusion.Server --launch-profile http   # http://localhost:5180 · demo / clinica2026
```

Test: `dotnet test --project tests/ClinicalSensorFusion.Core.Tests` e `dotnet test --project tests/ClinicalSensorFusion.Server.Tests`.

> ⚠️ Prima di installare su un iPhone che ha ancora l'app originale leggere
> [Migrazione dall'app originale](docs/architettura.md#8-migrazione-dallapp-originale--attenzione).
