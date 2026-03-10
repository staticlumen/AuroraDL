# AuroraDL (Music Spectrometer)

Native Windows desktop app that captures whole-system audio using WASAPI loopback (default playback device). No browser screen-share prompt.

## Features

- Real-time spectrometer overlay from system audio
- WASAPI loopback capture for the default playback device (no mic required)
- Calibrated STFT amplitude and dBFS display
- Optional track recognition via ACRCloud
- Optional downloads via Telegram and YouTube (yt-dlp + ffmpeg)

## Requirements

1. Windows 10/11.
2. .NET 8 SDK (x64).
   - https://dotnet.microsoft.com/download/dotnet/8.0

Optional (for downloads and recognition):

- ACRCloud credentials
- Telegram API credentials
- `yt-dlp.exe` + `ffmpeg.exe` in a `tools` folder

## Quick Start

1. Double-click `run-native.bat` (or run `run-native.ps1`).
2. Click `Start`.
3. Play audio on your Windows machine.

The app captures from the current default output endpoint (headphones/speakers) using loopback.

## Run From CLI

```powershell
dotnet run --project AuroraDL/auroradl.csproj -c Release
```

## Optional Setup (Recognition + Downloads)

You can add credentials inside the app (Controls page) or set environment variables.
The app stores values in `%LOCALAPPDATA%\auroradl\env-settings.json` (lightly obfuscated).

Required keys:

- `ACR_HOST` (for example: `identify-*.acrcloud.com`)
- `ACR_ACCESS_KEY`
- `ACR_ACCESS_SECRET`
- `TG_API_ID`
- `TG_API_HASH`
- `TG_PHONE` (format `+1234567890`)

Tools:

- Create a `tools` folder at the repo root (or next to the built exe).
- Place `yt-dlp.exe` and `ffmpeg.exe` inside `tools`.

## How It Works (Code Tour)

- `Program.cs` sets up crash logging and starts the WinForms app.
- `AudioLoopbackEngine.cs` captures system audio via WASAPI loopback and converts it to mono float samples.
- `SpectralAnalyzer.cs` applies a Hann window, runs the FFT, and computes single-sided magnitudes in dBFS.
- `Fft.cs` is a straightforward in-place radix-2 FFT.
- `MainForm.cs` is the core UI and workflow:
  - Maintains a rolling audio buffer.
  - Converts spectral bins into a smooth, logarithmic frequency curve for display.
  - Triggers periodic recognition attempts and manages download queues.
- `AcrCloudClient.cs` builds a WAV payload, signs the request, and parses ACRCloud results.

## Math/Calibration

The native app uses a calibrated STFT approach:

- Hann window: `w[n] = 0.5 - 0.5 cos(2πn/N)`
- FFT on windowed frame
- Single-sided amplitude:
  - `A[k] = |X[k]| / Σw[n]` for DC and Nyquist
  - `A[k] = 2|X[k]| / Σw[n]` for other bins
- dBFS:
  - `dBFS[k] = 20 log10(max(A[k], 1e-12))`

## Project Layout

- `AuroraDL/auroradl.csproj`
- `AuroraDL/auroradl.sln`
- `run-native.bat`
- `run-native.ps1`
- `auroradl.sln`

## Notes

- Switching the default Windows playback device while running may require stop/start.
- The older browser-based files remain in the repo but are not the recommended path.
