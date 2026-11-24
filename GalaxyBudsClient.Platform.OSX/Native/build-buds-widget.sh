#!/bin/bash
# Builds libBudsWidget.dylib (Liquid Glass desktop widget).

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
    -framework QuartzCore \
    -install_name "@rpath/libBudsWidget.dylib" \
    -o "$OUT_DIR/libBudsWidget.dylib" \
    src/BudsWidget.mm

echo "Built $OUT_DIR/libBudsWidget.dylib"
