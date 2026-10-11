#!/usr/bin/env bash
# Compila MediaPipeBridge.xcframework (device + simulatore) consumato dal binding .NET.
# Requisiti (macOS): Xcode 26+, CocoaPods (`brew install cocoapods`), XcodeGen (`brew install xcodegen`).
set -euo pipefail

cd "$(dirname "$0")"
OUT="build"
CONFIG="${CONFIGURATION:-Release}"

for tool in xcodegen pod xcodebuild; do
  command -v "$tool" >/dev/null || { echo "Manca '$tool'. Vedi docs/architettura.md (Prerequisiti)."; exit 1; }
done

xcodegen generate --quiet
pod install --silent

rm -rf "$OUT"
for platform in "iOS" "iOS Simulator"; do
  archive="$OUT/$(echo "$platform" | tr ' ' '_').xcarchive"
  xcodebuild archive \
    -workspace MediaPipeBridge.xcworkspace \
    -scheme MediaPipeBridge \
    -configuration "$CONFIG" \
    -destination "generic/platform=$platform" \
    -archivePath "$archive" \
    SKIP_INSTALL=NO \
    -quiet
done

xcodebuild -create-xcframework \
  -framework "$OUT/iOS.xcarchive/Products/Library/Frameworks/MediaPipeBridge.framework" \
  -framework "$OUT/iOS_Simulator.xcarchive/Products/Library/Frameworks/MediaPipeBridge.framework" \
  -output "$OUT/MediaPipeBridge.xcframework"

echo "OK: $(pwd)/$OUT/MediaPipeBridge.xcframework"
