# FilmRefit

FilmRefit is a desktop transcoding tool for creating timecode-preserving proxies and mezzanine media for post-production workflows.

![FilmRefit.jpg](presentation-media/FilmRefit.jpg)

## Overview

FilmRefit combines an Avalonia desktop interface with a Python/FFmpeg transcoding engine. It is built for camera-media preparation, especially Sony mirrorless footage, with a focus on preserving original source timecode through proxy and high-quality intermediate generation.

The app can load individual files or directories, inspect clip metadata, show thumbnails and preview frames, and batch-create editing-friendly outputs next to the original media.

## Features

- Load files, directories, or recursive directory trees.
- Display clip metadata such as resolution, frame rate, duration, codec, pixel format, bit depth, audio codec, camera/lens metadata, and source timecode.
- Preview selected clips with play/pause, seeking, frame stepping, and playback-rate controls.
- Generate DNxHR SQ 1080p proxy files.
- Generate DNxHR HQX mezzanine files at source resolution and bit depth.
- Preserve source timecode using the Python transcoder probe and parsing layer.
- Show per-file batch progress, overall progress, ETA, success, and failure status.
- Support a command-line transcoding path through `transcoder/filmrefit.py`.

## Project Structure

```text
src/FilmRefit.App/        Avalonia desktop application
transcoder/               Python FFmpeg transcoding and probe engine
tools/                    Development, build, run, publish, and icon scripts
docs/                     Architecture and implementation notes
packaging/                Linux, macOS, and web packaging assets
```

## Requirements

- .NET 10 SDK for building and running the desktop app from source.
- Python 3.
- FFmpeg and FFprobe available on `PATH`.

The current implementation expects FFmpeg to be provided by the system rather than vendored into the repository.

## Quick Start

Clone the repository, then build and run the desktop app:

```bash
./tools/build-app.sh
./tools/run-app.sh
```

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

## Releases

GitHub Actions builds release packages when a `v*` tag points to a commit on
`main`. Release builds create Windows x64, Linux x64, macOS Apple Silicon, and
macOS Intel artifacts. They also package pinned FFmpeg/FFprobe binaries and a
PyInstaller-built `filmrefit-transcoder` executable.

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

## Timecode

FilmRefit treats the Python transcoder as the source of truth for media probing and timecode detection. The desktop app does not infer timecode from filenames, file timestamps, or other non-timecode metadata.

## Development

FilmRefit's Python runtime target is pinned in `.python-version`. For local source
development, create a Python 3.11 virtual environment at the repository root:

```bash
python3.11 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
```

The desktop app automatically uses `.venv` when it exists, then falls back to the
system Python executable.

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

## Status

FilmRefit is early-stage software. The current baseline is CPU-only and GPU-vendor-independent, with optional hardware acceleration paths being explored where they fit the workflow without becoming hard requirements.

## License

FilmRefit is licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE).
