# Galaxy Buds Widget for macOS

> **English** | [简体中文](README.zh-CN.md)

[![Platform](https://img.shields.io/badge/platform-macOS%20(Apple%20Silicon)-blue.svg)](https://github.com/ARRIIIIIS/galaxy-buds-widget-macos/releases)
[![Release](https://img.shields.io/github/v/release/ARRIIIIIS/galaxy-buds-widget-macos)](https://github.com/ARRIIIIIS/galaxy-buds-widget-macos/releases/latest)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-green.svg)](https://www.gnu.org/licenses/gpl-3.0)

**Galaxy Buds Widget for macOS** is an open-source, native macOS **Liquid Glass desktop widget**, **9-band Equalizer**, and **neck posture monitor** for Samsung Galaxy Buds, built on top of [GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient).

Samsung does not provide an official Galaxy Wearable / Galaxy Buds desktop app for macOS. This project brings native Apple Silicon (`arm64`) desktop control—live L/R/Case battery levels, Active Noise Cancellation (ANC / Adaptive / Ambient / Off), Samsung EQ presets + custom 9-band EQ, Seamless Connection & Multipoint, and earbud IMU posture tracking—directly to your Mac desktop.

![Galaxy Buds Desktop Widget](screenshots/widget.jpg)

---

## Why Galaxy Buds Widget for macOS?

| Capability | Official Samsung App on Mac | Standard Desktop Clients | **Galaxy Buds Widget for macOS** |
| :--- | :--- | :--- | :--- |
| **macOS Support** | None (Windows / Mobile only) | Full window app | **Native `AppKit` Liquid Glass floating desktop widget** |
| **ANC / Ambient Control** | Not available on Mac | Inside settings window | **1-click animated pill switcher on desktop (`Off / Adaptive / ANC / Ambient`)** |
| **Battery Telemetry** | macOS Bluetooth menu (no case battery) | Window view | **Live Left, Right & Charging Case battery on desktop** |
| **Equalizer (EQ)** | Not available on Mac | Basic presets | **Samsung presets + interactive 9-band bar & spline EQ (`63 Hz – 16 kHz`)** |
| **Multipoint / Seamless** | Not available on Mac | Manual toggle | **Seamless Connection toggle + auto-restore Multipoint on connect** |
| **Ergonomics** | Not available | Not available | **IMU-based Neck Posture Monitor with screen-blur slouch alert** |
| **Languages** | — | Multi-page UI | **English (default) & Simplified Chinese (`中文`) live switch** |

---

## Supported Samsung Galaxy Buds Models

All major Samsung Galaxy Buds generations and color variants (with left/right 3D product render previews) are supported:

- **Galaxy Buds4 Pro** & **Galaxy Buds4**
- **Galaxy Buds3 Pro**, **Galaxy Buds3**, & **Galaxy Buds3 FE**
- **Galaxy Buds2 Pro** & **Galaxy Buds2**
- **Galaxy Buds Pro**
- **Galaxy Buds Live**
- **Galaxy Buds+** & **Galaxy Buds**

---

## Key Features

- **Native Liquid Glass Desktop Widget (`AppKit` / `NSVisualEffectView`)**: Compact `344×162` translucent floating widget pinned cleanly to your desktop, featuring a `1px` specular edge highlight, caustic glow on click, and `0.25s` long-press drag protection.
- **Smooth 4-Mode Noise Control**: Instant switching between **Off**, **Adaptive**, **ANC**, and **Ambient** with animated pill transitions.
- **Equalizer (Samsung Presets & 9-Band Custom)**: Choose from **Off**, **Bass Boost**, **Soft**, **Dynamic**, **Clear**, **Treble Boost**, or drag the interactive 9-band bar & curve chart (`63 Hz` to `16 kHz`, `-10 dB` to `+10 dB`) to tune your sound in real time.
- **Seamless Connection & Multipoint**: Enable Samsung Seamless Earbud Connection and automatically restore multipoint pairing state whenever your earbuds connect to your Mac.
- **Neck Posture Monitor (IMU Telemetry)**: Uses your earbuds' built-in IMU quaternion sensor to detect forward head tilt / slouching and gently blurs the screen as a reminder—complete with one-click calibration, trigger angle threshold, and blur intensity controls.
- **Minimalist Menu Bar + Web Workbench**: A 3-item menu bar tray (`Settings Wizard...`, `Restart`, `Quit`) paired with a local web setup workbench for Bluetooth pairing, 3D earbud render preview, EQ tuning, and posture settings.
- **Multi-Language (English Default + 中文)**: Defaults to English across the desktop widget, menu bar, and web workbench, with instant switching to Simplified Chinese (`中文`).

---

## Download & Installation (macOS Apple Silicon)

1. Download the latest pre-built app archive (`Galaxy-Buds-Widget-macOS-v1.0.0-arm64.zip`) from [**GitHub Releases**](https://github.com/ARRIIIIIS/galaxy-buds-widget-macos/releases/latest).
2. Unzip and move **`Galaxy Buds Manager.app`** to your **`/Applications`** folder.
3. Because this app is ad-hoc signed rather than notarized with a paid Apple Developer ID, if macOS blocks the first launch, run:
   ```bash
   xattr -cr "/Applications/Galaxy Buds Manager.app"
   ```
   Then open **Galaxy Buds Manager** from `/Applications` or Spotlight.

---

## Frequently Asked Questions (FAQ)

### Is there an official Samsung Galaxy Buds app for macOS?
No. Samsung only distributes Galaxy Wearable on Android/iOS and Galaxy Buds on Windows. **Galaxy Buds Widget for macOS** bridges that gap natively on Apple Silicon Macs (`arm64`) using reverse-engineered Samsung Bluetooth SPP/BLE protocols from `GalaxyBudsClient`.

### How do I see Galaxy Buds charging case battery on Mac?
macOS's built-in Bluetooth menu only shows combined or earbud battery percentages. Once connected via **Galaxy Buds Widget for macOS**, the desktop widget and web workbench display **Left (`L`)**, **Right (`R`)**, and **Charging Case (`Case`)** percentages in real time.

### How can I customize Galaxy Buds EQ on macOS?
Open **Settings Wizard...** from the menu bar tray, go to step **02 Audio & Multipoint**, and select any Samsung preset (**Bass Boost**, **Soft**, **Dynamic**, **Clear**, **Treble Boost**) or click **Custom** to drag the 9-band (`63 Hz – 16 kHz`) equalizer sliders.

### How does the Neck Posture Monitor work?
Compatible Galaxy Buds stream head-tracking IMU orientation data over Bluetooth. Click the **Calibrate** icon on the widget (or in step **03 Posture & Widget**) while sitting upright; when your head pitches past the trigger angle, macOS smoothly applies a screen-blur reminder until you sit up.

---

## Build from Source

### Requirements
- macOS on Apple Silicon (`arm64`)
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Xcode Command Line Tools (`clang++`)

### Steps
1. Build the native macOS Liquid Glass widget dynamic library:
   ```bash
   ./GalaxyBudsClient.Platform.OSX/Native/build-buds-widget.sh
   ```
2. Publish the `osx-arm64` app bundle:
   ```bash
   dotnet publish GalaxyBudsClient/GalaxyBudsClient.csproj -c Release -r osx-arm64 \
     -p:UseMacOSSDK=true \
     -p:AllowMissingPrunePackageData=true \
     -p:ValidateXcodeVersion=false
   ```
3. Sign `Contents/MonoBundle/*.dylib` and the `.app` bundle ad-hoc if running locally.

---

## License

Licensed under **GPLv3**, following the upstream [GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient) project.
