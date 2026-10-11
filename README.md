# clinicalfusion

Ricostruzione di **Clinical Sensor Fusion**, app iOS in .NET MAUI (.NET 11) il cui sorgente originale è andato perso.

L'app acquisisce una rilevazione clinica fondendo **MediaPipe** (Pose, Hands, Face) con **tutti i sensori
dell'iPhone** — LiDAR, fotocamere posteriore e TrueDepth, IMU a 100 Hz, barometro, pedometro, microfono,
prossimità, luce, GPS opzionale — calcola indicatori (escursione articolare 3D, tremore, cammino, voce),
li salva in un archivio locale cifrato e li fa commentare a un modello linguistico su un server **Ollama**.

## Documentazione

- [docs/funzionalita-ricostruite.md](docs/funzionalita-ricostruite.md): cosa è stato ricavato dall'app installata.
- [docs/architettura.md](docs/architettura.md): architettura, sensori, fusione, sicurezza, build, migrazione.

## Avvio rapido (Mac con Xcode 26 e .NET 11)

```bash
brew install cocoapods xcodegen
dotnet workload install maui-ios
./scripts/download-models.sh
dotnet build src/ClinicalSensorFusion.App -f net11.0-ios -t:Run
```

Test della logica (qualsiasi OS): `dotnet test --project tests/ClinicalSensorFusion.Core.Tests`.

> ⚠️ Prima di installare su un iPhone che ha ancora l'app originale leggere
> [Migrazione dall'app originale](docs/architettura.md#8-migrazione-dallapp-originale--attenzione).
