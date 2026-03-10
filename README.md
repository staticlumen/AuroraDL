# AuroraDL (Music Spectrometer)

Native Windows desktop app that captures whole-system audio using WASAPI loopback (default playback device). No browser screen-share prompt.

## Features

- Real-time spectrometer visualization from system audio
- WASAPI loopback capture for the default playback device
- Calibrated STFT amplitude and dBFS display

## Requirements

1. Windows 10/11.
2. .NET 8 SDK (x64).
   - https://dotnet.microsoft.com/download/dotnet/8.0

## Quick Start

1. Double-click `run-native.bat`.
2. Click `Start`.
3. Play audio on your Windows machine.

The app captures from the current default output endpoint (headphones/speakers) using loopback.

## Project Layout

- `AuroraDL/auroradl.csproj`
- `AuroraDL/auroradl.sln`
- `run-native.bat`
- `run-native.ps1`
- `auroradl.sln`

## Math/Calibration

The native app uses a calibrated STFT approach:

- Hann window: `w[n] = 0.5 - 0.5 cos(2πn/N)`
- FFT on windowed frame
- Single-sided amplitude:
  - `A[k] = |X[k]| / Σw[n]` for DC and Nyquist
  - `A[k] = 2|X[k]| / Σw[n]` for other bins
- dBFS:
  - `dBFS[k] = 20 log10(max(A[k], 1e-12))`

## Notes

- If you switch the default Windows playback device while running, stop/start capture.
- The older browser-based files remain in the repo but are not the recommended path.
