# AuroraDL

### High-Quality Music & Video Downloader for Windows

AuroraDL is a **native Windows desktop downloader** designed to extract **high-quality audio and video** from online media sources.

Unlike many download tools that produce incompatible or low-quality files, AuroraDL focuses on generating **clean, editor-friendly media** suitable for music libraries, DJs, and video editing workflows.

The project originally began as an audio spectrometer experiment, but evolved into a **powerful media acquisition tool** optimized for quality.

---

## What Makes AuroraDL Different

Most download tools focus on speed.
AuroraDL focuses on **output quality and usability**.

AuroraDL aims to produce files that:

* preserve the highest available bitrate
* remain compatible with editing software
* avoid unnecessary recompression
* integrate cleanly into music or video workflows

---

## Core Features

* Download **high-quality music and video**
* Select the **best available streams**
* Produce **clean MP4 / audio files**
* Designed for **Windows desktop workflows**
* Built with **.NET and modern tooling**

Bonus capability:

* Real-time **audio spectrometer visualization** from system audio

---

## Example Use Cases

AuroraDL works well for:

* Building a **high-quality personal music library**
* Extracting tracks for **DJ sets**
* Downloading **video sources for editing**
* Capturing media in formats compatible with editing tools

---

## Demo

*(Add screenshots or a short GIF here)*

Example:

![AuroraDL Interface](screenshots/interface.png)

---

## Quick Start

Requirements

* Windows 10 / 11
* .NET 8 SDK (x64)

Download .NET if needed:

https://dotnet.microsoft.com/download/dotnet/8.0

Run the application

```id="runexample1"
run-native.bat
```

Then:

1. Launch AuroraDL
2. Paste a media link
3. Download high-quality audio or video

---

## Project Structure

```id="runexample2"
AuroraDL/
 ├─ auroradl.csproj
 ├─ auroradl.sln
 ├─ run-native.bat
 ├─ run-native.ps1
 ├─ Downloader/
 ├─ Math/
 └─ ...
```

---

## Technology

AuroraDL integrates several tools and systems:

* .NET 8 desktop application
* Modern media extraction pipelines
* Windows-native execution environment
* Optional DSP visualization components

---

## Project History

AuroraDL began as an experiment in **real-time audio spectrometry**, using WASAPI loopback capture and STFT analysis.

While developing the audio pipeline, the project expanded into a **high-quality media downloader**, which became the primary focus.

The spectrometer remains as an experimental component.

---

## Notes

Some experimental browser-based files remain in the repository from early prototypes but are no longer the recommended path.

---

## License

MIT
