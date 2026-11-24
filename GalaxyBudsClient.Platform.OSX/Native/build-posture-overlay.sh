#!/bin/bash
# Builds libPostureOverlay.dylib (posture blur overlay).
# Mirrors what xcodebuild does for NativeInterop, but kept as its own small
# target so the Xcode project does not have to be edited.

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

SDK="$(xcrun --show-sdk-path)"
OUT_DIR="$SCRIPT_DIR/Build/Release"
mkdir -p "$OUT_DIR"

ARCH="${1:-$(uname -m)}"

clang++ -dynamiclib \
    -fobjc-arc \
    -O2 \
    -arch "$ARCH" \
    -mmacosx-version-min=12.0 \
    -isysroot "$SDK" \
    -framework Cocoa \
    -framework CoreGraphics \
    -install_name "@rpath/libPostureOverlay.dylib" \
    -o "$OUT_DIR/libPostureOverlay.dylib" \
    src/PostureOverlay.mm

echo "Built $OUT_DIR/libPostureOverlay.dylib"
