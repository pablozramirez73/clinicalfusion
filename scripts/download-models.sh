#!/usr/bin/env bash
# Scarica i modelli MediaPipe (.task) nelle risorse dell'app. Non sono versionati (≈20 MB, licenza Apache 2.0).
set -euo pipefail

DEST="$(cd "$(dirname "$0")/.." && pwd)/src/ClinicalSensorFusion.App/Resources/Raw/models"
BASE="https://storage.googleapis.com/mediapipe-models"
mkdir -p "$DEST"

download() {
  local name="$1" url="$2"
  if [[ -s "$DEST/$name" ]]; then
    echo "già presente: $name"
    return
  fi
  echo "scarico: $name"
  curl -fsSL --retry 3 -o "$DEST/$name.part" "$url"
  mv "$DEST/$name.part" "$DEST/$name"
}

download pose_landmarker_full.task "$BASE/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task"
download hand_landmarker.task      "$BASE/hand_landmarker/hand_landmarker/float16/latest/hand_landmarker.task"

echo "Modelli in $DEST"
