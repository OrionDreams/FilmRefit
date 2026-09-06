# Developer Guide

This document collects source-build, architecture, packaging, and command-line details for FilmRefit contributors.

## Architecture

FilmRefit combines an Avalonia desktop interface with a Python/FFmpeg transcoding engine. The desktop app handles user interaction, batching, preview, and process orchestration. The Python transcoder is responsible for media probing, timecode detection, and FFmpeg command construction.

The desktop app treats the Python transcoder as the source of truth for media probing and timecode detection. It should not infer timecode from filenames, file timestamps, or other non-timecode metadata.

The current baseline is CPU-only and GPU-vendor-independent, with optional hardware acceleration paths being explored where they fit the workflow without becoming hard requirements.

Measure before optimizing. Keep acceleration paths optional unless the project requirements explicitly change.

## Project Structure

```text
src/FilmRefit.App/        Avalonia desktop application
transcoder/               Python FFmpeg transcoding and probe engine
resolve_importer/         DaVinci Resolve Lua proxy-linking utility
tools/                    Development, build, run, publish, and icon scripts
docs/                     Architecture and implementation notes
packaging/                Linux, macOS, and web packaging assets
```

## Requirements

- .NET 10 SDK for building and running the desktop app from source.
- Python 3.
- FFmpeg and FFprobe available on `PATH`.

The current source workflow expects FFmpeg to be provided by the system rather than vendored into the repository.

## Python Environment

FilmRefit's Python runtime target is pinned in `.python-version`. For local source development, create a Python 3.11 virtual environment at the repository root:

```bash
python3.11 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
```

The desktop app automatically uses `.venv` when it exists, then falls back to the system Python executable.

## Desktop App

Build the solution:

```bash
./tools/build-app.sh
```

Run the app:

```bash
./tools/run-app.sh
```

Run without rebuilding first:

```bash
BUILD_BEFORE_RUN=0 ./tools/run-app.sh
```

The desktop app uses:

- Avalonia UI 12
- .NET 10
- CommunityToolkit.Mvvm
- Python 3
- FFmpeg / FFprobe

## Command-Line Transcoding

The Python transcoder can also be used directly.

Create a DNxHR HQX mezzanine file:

```bash
python3 transcoder/filmrefit.py C2787.MP4
```

Create a DNxHR SQ proxy:

```bash
python3 transcoder/filmrefit.py --mode proxy C2787.MP4
```

Process a directory:

```bash
python3 transcoder/filmrefit.py --mode proxy "/path/to/footage"
```

Use a custom file pattern:

```bash
python3 transcoder/filmrefit.py \
  --mode proxy \
  --pattern '.*\.(MP4|MOV)' \
  "/path/to/footage"
```

Generated outputs are written next to the source media:

- Proxy: `<source>_PROXY.mov`
- Mezzanine: `<source>_MEZZANINE.mov`

## Releases

GitHub Actions builds release packages when a `v*` tag points to a commit on `main`. Release builds create Windows x64, Linux x64, macOS Apple Silicon, and macOS Intel artifacts. They also package pinned FFmpeg/FFprobe binaries and a PyInstaller-built `filmrefit-transcoder` executable.

To publish a self-contained app for the current platform:

```bash
./tools/publish-app.sh
```

The publish output is written to:

```text
artifacts/publish/<runtime-identifier>
```

You can override the target runtime:

```bash
RUNTIME_IDENTIFIER=linux-x64 ./tools/publish-app.sh
```

## More Notes

- [CSHARP-DOTNET-AVALONIA-MVVM.md](CSHARP-DOTNET-AVALONIA-MVVM.md)
- [SCRIPTING.md](SCRIPTING.md)
- [PLAYBACK.md](PLAYBACK.md)
