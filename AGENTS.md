# AGENTS.md

Instructions for Codex and other coding agents working in this repository.

## Read first

Before changing code, read:

1. `docs/CSHARP-DOTNET-AVALONIA-MVVM.md` for C#, .NET and Avalonia guidance
2. `docs/SCRIPTING.md` for the python transcoding engine
3. `docs/PLAYBACK.md` for playback related implementation

## Project goal

Offer features for Video transcoding, proxies, and media preparation for post-production. One key quality of this software is that it preserves the original timecode across all the transcoding and transformation options that it offers.

Timecode detection and interpretation belongs in the Python transcoder, not in the C# Avalonia wrapper. Do not infer timecode from filenames, recording timestamps, or other non-timecode fields. The wrapper should use the Python probe interface for display metadata.

The baseline must remain CPU-only and GPU-vendor-independent. Optional CPU and GPU acceleration may be explored, but for the initial implementation, the only required acceleration option is intel quicksync decoding of H.264 10-bit 4:2:2 and 4:2:0 video on Linux through vaapi. Ideally this software auto-detects if this acceleration and others are available on the current machine.

## Dependencies

Ideally the number of dependencies should be kept to a minimum. Therefore, this project should use existing libraries for big and well-known functionalities. For smaller, lesser-known, and more obscure functionalities, an in-house implementation is preferred. 

For ffmpeg, ideally it would not be delivered with this application, but first-run setup or an installer should make it easy for the end user to install all the dependencies. This can mean that on Linux this application would need to be delivered in distro-specific packages.

## Performance philosophy

Measure before optimizing. Use representative clips for measurements.

Prioritize portable CPU optimizations:

1. eliminate redundant computation and allocations;
2. investigate multiprocessing/chunking only after single-process hotspots are measured;
3. do not add mandatory CUDA/ROCm/VAAPI/NVDEC dependencies.

When optimizing, compare both runtime and fps on known clips.

## Files agents may add

Small benchmark scripts, unit tests, fixtures using synthetic arrays and additional Markdown engineering notes are welcome. Do not vendor large video files into the repository.



