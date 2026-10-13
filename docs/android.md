# SensorFusion Clinic per Android

Stessa app, stesso archivio, stesso server: il progetto MAUI `ClinicalSensorFusion.App` ha ora anche il target
`net11.0-android`. Interfaccia, test standardizzati, guida vocale, analisi Ollama, sincronizzazione e dashboard
sono codice condiviso; cambia solo lo strato che parla con l'hardware (`Platforms/Android`).

iOS resta la piattaforma di riferimento per le misure in metri: Android non ha un equivalente diffuso di LiDAR e
TrueDepth, e questa versione non li sostituisce con altro.

## 1. Cosa cambia rispetto a iOS

| | iOS | Android |
|---|---|---|
| Fotocamera | ARKit | CameraX (anteprima + analisi 640×480 RGBA) |
| Profondità | LiDAR a richiesta (modalità corpo) | **assente**: l'interruttore non compare |
| Punti 3D e angoli articolari | LiDAR, oppure stima 3D di MediaPipe | sempre stima 3D di MediaPipe (`sorgente = 1`) |
| Distanza del soggetto, copertura LiDAR | misurate | non disponibili: distanza NaN, copertura assente dal riepilogo (non 0%) |
| Blendshape del volto | ARKit TrueDepth, 52 coefficienti | MediaPipe Face, riordinate nel layout ARKit; `tongueOut` = NaN |
| MediaPipe | bridge Swift, pod 0.10.21, GPU Metal | facciata Java, Maven 0.10.35, GPU OpenGL con ripiego su CPU |
| Dove gira l'inferenza | sempre GPU | mostrato nello stato dei sensori: «MediaPipe Pose · GPU» oppure «· CPU» |
| IMU 100 Hz | CMDeviceMotion | accelerazione lineare + gravità + giroscopio + vettore di rotazione + magnetometro |
| Barometro | CMAltimeter | `TYPE_PRESSURE` (quota relativa dalla prima lettura) |
| Pedometro | passi, distanza, cadenza, piani | solo passi (`TYPE_STEP_COUNTER`) |
| Luce ambientale | stima ARKit (lumen, K) | sensore di luce (lux); temperatura di colore assente |
| Audio | AVAudioEngine *measurement* → CAF | AudioRecord `UNPROCESSED` → CAF scritto da `CafWriter` |
| Chiave del database | Portachiavi | Android Keystore (AES-GCM) |
| Blocco dell'app | Face ID / Touch ID / codice | impronta o volto / blocco schermo (BiometricPrompt) |
| Cookie Cloudflare Access | WKWebView → NSHTTPCookieStorage | WebView → `CookieContainer` dei client HTTP |
| HTTP solo in rete locale | App Transport Security | `LocalCleartextHandler` su ogni richiesta |

I layout dei flussi (`SensorKind`) sono identici: una rilevazione Android si legge, si sincronizza e si rivede nella
dashboard come una iOS. Le differenze da tenere presenti in analisi:

- **Convenzione dell'IMU.** Android misura in m/s² la forza che sostiene il telefono, iOS in g l'opposto: i valori
  sono convertiti alla convenzione iOS. L'assetto usa però il riferimento geografico di Android (X est, Y nord):
  gli angoli rispetto alla verticale coincidono con iOS, l'imbardata assoluta no.
- **Luce ambientale.** Lux misurati contro lumen stimati: i valori dei due sistemi non sono confrontabili.
- **Stato termico.** Le sette classi di Android sono riportate alle quattro di iOS (0 nominale … 3 critico).
- **Coordinate dei punti.** Come su iOS, MediaPipe usa la rotazione solo per l'inferenza e restituisce i punti nel
  sistema del sensore: così sono archiviati, e solo l'overlay li raddrizza per la vista.

## 2. Struttura

```
src/ClinicalSensorFusion.MediaPipe.Android/   binding .NET della facciata Java su MediaPipe Tasks Vision
  Java/com/lucaguelfi/csf/vision/             CsfVisionEngine, CsfVisionOptions, CsfVisionResult
src/ClinicalSensorFusion.App/Platforms/Android/
  Vision/      CameraController (CameraX), VisionPipeline (flussi e overlay)
  Sensors/     IMU, barometro, contapassi, luce, prossimità/stato, GPS, microfono
  Security/    KeystoreKeyProvider, AndroidDeviceAuthenticator, AndroidLockScreen
  Services/    AndroidCaptureEngine, AndroidAudioPlayback, AndroidWebSessionBridge
  Rendering/   CapturePreviewHandler (PreviewView)
src/ClinicalSensorFusion.Core/
  Signal/CafFile.cs                 scrittura e lettura dei file CAF fuori da iOS
  Fusion/FaceBlendshapeLayout.cs    nomi MediaPipe → layout ARKit
```

Come su iOS, a .NET si lega solo una facciata piccola: la libreria di Google arriva da Maven così com'è
(`Bind="false"`) e i risultati attraversano il confine Java/.NET come pochi array di float per fotogramma.

## 3. Sicurezza e privacy

- Database cifrato (SQLCipher 4) con chiave casuale a 256 bit avvolta da una chiave AES dell'Android Keystore, che
  non lascia l'hardware sicuro del telefono. Se la chiave va persa l'archivio non è più leggibile, e non viene
  sovrascritto (`DatabaseKeyMismatchException`, come su iOS).
- `allowBackup="false"` e regole di estrazione vuote: archivio, audio e chiave non entrano nei backup di Google né
  nel trasferimento a un altro telefono. I file stanno nella cartella privata dell'app, cifrata dal sistema.
- Con il blocco dell'app attivo la finestra è `FLAG_SECURE`: niente contenuto nelle app recenti né negli screenshot.
- Il traffico in chiaro è abilitato nel manifest (Android non sa limitarlo alla rete locale) ma ogni richiesta
  dell'app passa da `LocalCleartextHandler`, che rifiuta HTTP fuori dalla rete locale. Solo certificati di sistema.
- Permessi: fotocamera e microfono obbligatori; attività fisica (contapassi) e posizione facoltativi.

## 4. Build

Prerequisiti: .NET 11 con `dotnet workload install maui-android`, JDK 21, Android SDK (piattaforma 37).
Android minimo: 11 (API 30); destinazione: Android 16 (API 36), dichiarata nel manifest.

```bash
./scripts/download-models.sh
dotnet build src/ClinicalSensorFusion.App -f net11.0-android               # APK di debug in bin/Debug/net11.0-android
dotnet build src/ClinicalSensorFusion.App -f net11.0-android -t:Run       # installa e avvia sul dispositivo collegato
dotnet publish src/ClinicalSensorFusion.App -f net11.0-android -c Release # da firmare con la propria chiave
```

Su Linux e Windows il progetto ha solo il target Android; su macOS anche quello iOS.

## 5. Cosa è verificato e cosa no

Verificato:
- compilazione di binding e app, APK installato su emulatore Android 15 (arm64);
- avvio, archivio cifrato riaperto dopo il riavvio dell'app (Keystore), impostazioni;
- anteprima CameraX e inferenza MediaPipe per corpo, mani e volto (sull'emulatore la GPU non regge i modelli:
  esercitato il ripiego su CPU);
- una rilevazione completa: IMU a 100 campioni/s, audio CAF con intestazione corretta, riepilogo e affidabilità;
- test del Core per `CafWriter`/`CafReader` e per il layout delle blendshape; il server legge i CAF di Android.

Da verificare su un telefono reale:
- riconoscimento con una persona inquadrata (la scena dell'emulatore non ne ha) e allineamento dell'overlay,
  in particolare con la fotocamera frontale (specchiata);
- inferenza su GPU, frequenza dei fotogrammi e riscaldamento;
- microfono (`UNPROCESSED` non c'è su tutti i modelli), contapassi, barometro, prossimità;
- blocco biometrico e accesso remoto via WebView.

Come la versione iOS, **non è un dispositivo medico certificato**; senza profondità metrica gli angoli sono una
stima da immagine e vanno letti con più cautela di quelli ottenuti con il LiDAR.
