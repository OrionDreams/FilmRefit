# SCRIPTING.md

## Purpose

This document captures the current state, constraints, and design goals for a custom Sony-camera transcoding/proxy workflow built around FFmpeg, Intel VAAPI hardware decoding, and DaVinci Resolve Studio on Linux.

The intended audience is a coding agent (e.g. Codex) that will continue evolving the scripts without needing the full conversation history.

---

## User environment

### Camera
- Sony A7 IV.
- Relevant recording formats:
  - XAVC HS 4K = H.265 / HEVC Long-GOP.
  - XAVC S 4K = H.264 / AVC Long-GOP.
  - XAVC S-I 4K = H.264 / AVC All-I.
- Typical camera files are named like:
  - `C2782.MP4`
  - regex default: `C\d+\.MP4`
- The user shoots:
  - 23.976p
  - 29.97p
  - 59.94p
- Sony files are often:
  - HEVC 4:2:2 10-bit, `yuv422p10le`
  - H.264 High 4:2:2 10-bit, `yuv422p10le`
- Sony files contain:
  - video
  - PCM audio
  - a Sony RTMD data stream
  - a source timecode tag on the RTMD stream

Example source probe:

```text
Video: hevc (Rext), yuv422p10le(pc), 3840x2160
r_frame_rate=24000/1001
```

Another 60p example:

```text
Video: hevc (Rext), yuv422p10le(pc), 3840x2160
r_frame_rate=60000/1001
TAG:timecode=05:48:34:10
```

---

## Computer / OS

This is a known configuration that should be prioritized during the initial development stages.

- Laptop CPU: Intel Core i9-11900H.
- RAM: 32 GB.
- GPU: NVIDIA RTX 3080 Laptop 8 GB.
- OS: CachyOS Linux.
- DaVinci Resolve Studio runs through `davincibox`.
- Resolve on Linux only exposes NVIDIA decoding in its UI; Intel Quick Sync is not available inside Resolve.
- Intel iGPU device to use is:

```text
/dev/dri/renderD129
```

Do not use `/dev/dri/renderD128` unless the user explicitly says the hardware layout changed.

---

## Important codec / hardware findings

### Intel VAAPI decode

The Intel iGPU and Linux media stack expose HEVC 4:2:2 10-bit decode support.

`vainfo` includes:

```text
VAProfileHEVCMain422_10 : VAEntrypointVLD
```

Pure HEVC 4:2:2 10-bit VAAPI decode benchmark:

```bash
ffmpeg \
  -hwaccel vaapi \
  -hwaccel_device /dev/dri/renderD129 \
  -hwaccel_output_format vaapi \
  -i "$INPUT" \
  -map 0:v:0 \
  -f null -
```

Observed throughput:

```text
~114 fps
```

### Hardware decode + system-memory download

This works:

```bash
ffmpeg \
  -hwaccel vaapi \
  -hwaccel_device /dev/dri/renderD129 \
  -hwaccel_output_format vaapi \
  -i "$INPUT" \
  -map 0:v:0 \
  -vf 'hwdownload,format=y210le' \
  -f null -
```

Observed throughput:

```text
~42 fps
```

This confirms that the decoder itself is much faster than the full decode+download pipeline.

### Important pixel-format behavior

For HEVC 4:2:2 10-bit VAAPI decode, the working system-memory download format is:

```text
y210le
```

The following did not work as direct `hwdownload` targets:

```text
yuv422p10le
p210le
```

So for high-quality DNxHR HQX conversion, use:

```text
hwdownload,format=y210le,format=yuv422p10le
```

Do not convert through NV12 for final/high-quality HQX output because NV12 is 8-bit 4:2:0.

---

## Performance findings

### Hardware decode + DNxHR HQX

Observed:

```text
~18 fps
```

Command pattern:

```bash
ffmpeg \
  -nostdin \
  -hwaccel vaapi \
  -hwaccel_device /dev/dri/renderD129 \
  -hwaccel_output_format vaapi \
  -extra_hw_frames 128 \
  -i "$INPUT" \
  -map 0:v:0 \
  -map 0:a? \
  -vf 'hwdownload,format=y210le,format=yuv422p10le' \
  -c:v dnxhd \
  -profile:v dnxhr_hqx \
  -pix_fmt yuv422p10le \
  -color_range pc \
  -c:a copy \
  "$OUTPUT"
```

### Hardware decode + ProRes

Observed:

```text
ProRes 422 HQ: ~10 fps
ProRes 422:    ~11 fps
```

So on this machine, DNxHR HQX is clearly faster to encode than ProRes through FFmpeg.

### Hardware HEVC re-encode

Intel HEVC 4:2:2 10-bit hardware encode worked with:

```text
-profile:v rext
```

but only around:

```text
~17 fps
```

even for All-I and short GOP experiments.

Main10 4:2:0 with Intel `-low_power 1` reached around:

```text
~42 fps
```

but `-low_power 1` is not supported for HEVC 4:2:2 10-bit RExt:

```text
No usable encoding entrypoint found for profile VAProfileHEVCMain422_10
```

Therefore, hardware HEVC re-encode does not provide a useful 4:2:2 10-bit editing intermediate on this hardware.

---

## Resolve workflow goals

The user wants two modes:

### `hqx`

Purpose:
- Generate a full-resolution, grading-friendly intermediate.
- Current target: DNxHR HQX.
- Preserve:
  - 4K
  - 10-bit
  - 4:2:2
  - source frame rate
  - source full-range handling
  - source timecode if possible

Expected output name:

```text
<NAME>_MEZZANINE.mov
```

### `proxy`

Purpose:
- Generate lightweight media for Resolve editing.
- Current target: 1080p DNxHR SQ.
- Proxy is only for editorial.
- Final grading/export should use originals or later HQX intermediates.
- It is acceptable for proxies to be 8-bit / 4:2:0 internally before DNxHR because they are not used for final color.

Expected output name:

```text
<NAME>_PROXY.mov
```

---

## Current proxy strategy

### HEVC sources

For HEVC proxies, scale on the Intel GPU before downloading to system memory:

```text
4K HEVC 4:2:2 10-bit
→ Intel VAAPI decode
→ Intel VAAPI scale to 1920x1080 NV12
→ hwdownload 1080p NV12
→ convert to yuv422p
→ DNxHR SQ
```

This was much more stable than downloading full-resolution Y210 first.

Working filter:

```text
scale_vaapi=w=1920:h=1080:format=nv12,hwdownload,format=nv12,format=yuv422p
```

Current proxy encode:

```text
-c:v dnxhd
-profile:v dnxhr_sq
-pix_fmt yuv422p
```

Originally DNxHR LB was used, but FFmpeg crashed during a long 60p encode with:

```text
Assertion s->buf_ptr < s->buf_end failed at libavcodec/put_bits.h
```

So proxy mode was moved to DNxHR SQ.

### H.264 sources

Sony H.264 High 4:2:2 10-bit currently falls back to software decode.

Use software scaling for proxies:

```text
scale=1920:-2,format=yuv422p
```

---

## Known VAAPI stability issue

Long-running HEVC VAAPI decode + `hwdownload` pipelines can sometimes fail with:

```text
Failed to sync surface ... 34 (HW busy now)
Failed to download frame: -5
Error while filtering: Input/output error
```

This occurred:
- with HQX transcodes
- with early proxy pipelines

`-extra_hw_frames` has been tested with:
- 64
- 128
- 256 suggested

Current script uses:

```text
-extra_hw_frames 128
```

No definitive proof yet that larger values fully solve the issue.

Important:
- Failed outputs must be deleted.
- Batch mode should continue to the next clip after a failure.

---

## Source color/range handling

Sony sample files probe as:

```text
pix_fmt=yuv422p10le
color_range=pc
color_space=unknown
color_transfer=unknown
color_primaries=unknown
```

Important rules:
- Preserve full range with:

```text
-color_range pc
```

- Do not invent Rec.709 / S-Log / S-Gamut metadata tags if they were not present.
- Do not apply LUTs, CSTs, gamma transforms, or color-space conversions during proxy/HQX generation.
- HQX path should remain a pure codec/transcoding operation.

---

## Sony metadata and timecode

### RTMD stream

Sony source files contain:

```text
Stream #0:2 Data: none (rtmd)
handler_name: Timed Metadata Media Handler
TAG:timecode=...
```

Example:

```text
TAG:timecode=05:48:34:10
```

Do not map the Sony RTMD stream into DNxHR MOV output.

Use:

```text
-map 0:v:0
-map 0:a?
```

and preserve the original source MP4 + XML forever.

### Timecode problem discovered

A proxy generated with:

```text
-timecode "05:48:34:10"
```

was rejected by Resolve:

```text
Please select a proxy clip with a matching framerate
and overlapping timecode with 'C2787.MP4'.
```

In Resolve:
- source displayed as `59.940 DF`
- proxy displayed as `59.940`

The textual timecode matched, but the proxy was non-drop-frame.

Repairing the proxy with:

```bash
ffmpeg \
  -nostdin \
  -i C2787_PROXY.mov \
  -map 0:v:0 \
  -map 0:a? \
  -c copy \
  -timecode "05:48:34;10" \
  C2787_PROXY_FIXED.mov
```

worked.

Therefore:
- `:` means NDF
- `;` means DF
- DF/NDF must be detected correctly, not guessed purely from FPS

### Strong user requirements

Do not infer drop-frame just because the rate is 29.97 or 59.94.

The script should determine the actual Sony camera timecode mode from metadata whenever possible.

Do not infer or synthesize source timecode from filenames, recording dates, creation times, or other non-timecode fields. Timecode is used so NLE software such as Resolve or Premiere can validate that a proxy or mezzanine belongs to a specific source clip. If no metadata timecode is found, leave output timecode unset.

Timecode detection and interpretation belongs in the Python transcoder only. The Avalonia app is a wrapper and should call the Python probe interface instead of duplicating ffprobe parsing or camera-specific timecode logic in C#.

---

## Sony XML / NonRealTimeMeta parsing

Sony clips may have a sidecar:

```text
C2787.MP4
C2787M01.XML
```

Sony metadata may also exist as embedded `NonRealTimeMeta` XML near the end of the MP4.

The current Python design searches:
1. sidecar XML first
2. embedded `NonRealTimeMeta` second

The relevant metadata structure is expected around:

```text
LtcChangeTable
LtcChange
```

The current implementation attempts to decode Sony LTC values from an 8-digit `FFSSMMHH`-style representation and derive DF/NDF from the frame-tens nibble.

This area should be treated as sensitive and verified carefully against known clips.

A known DF clip:

```text
C2787.MP4
ffprobe textual TC: 05:48:34:10
Resolve: 59.940 DF
desired MOV proxy TC: 05:48:34;10
```

If Sony metadata parsing is uncertain, do not guess DF/NDF from frame rate. For non-Sony files, a standard embedded metadata timecode such as MOV/MP4 `tmcd` may still be used directly when ffprobe exposes it.

---

## Source metadata probe interface

The Python script exposes source metadata for the Avalonia wrapper:

```bash
python3 transcoder/filmrefit.py --probe-json INPUT
```

This JSON path uses the same source-timecode resolver as transcoding:

1. Parse Sony LTC from sidecar XML or embedded `NonRealTimeMeta` when available on Sony-like sources.
2. Otherwise use embedded timecode tags reported by ffprobe, including standard `tmcd` timecode tracks.
3. Otherwise return no timecode.

The C# app should treat this JSON as display data only. It must not independently infer timecode or add fallbacks from filenames or recording timestamps.

Do not scan every non-Sony file for embedded Sony XML. Tail-scanning large MP4 files is expensive on network storage, so Sony LTC parsing should be gated by evidence such as a Sony sidecar XML file, an `rtmd` stream, or XAVC/NRAS container branding.

---

## Current Python architecture

The main script is being migrated from Bash to Python.

Preferred filename:

```text
video-to-dnxhr.py
```

The script should support both:
- single file input
- directory input

### CLI

Examples:

```bash
./video-to-dnxhr.py C2787.MP4
```

Defaults to HQX.

```bash
./video-to-dnxhr.py --mode proxy C2787.MP4
```

```bash
./video-to-dnxhr.py --mode proxy /path/to/folder
```

Directory filename filter:

```bash
--pattern REGEX
```

Default:

```text
C\d+\.MP4
```

Examples:

```bash
./video-to-dnxhr.py \
  --mode proxy \
  --pattern 'GX\d+\.MP4' \
  /path/to/folder
```

or:

```bash
./video-to-dnxhr.py \
  --mode proxy \
  --pattern '.*\.(MP4|MOV)' \
  /path/to/folder
```

Use Python `re.fullmatch()` for filename matching.

---

## Desired directory behavior

When input is a directory:

- only direct children for now; no recursive requirement unless added later
- filter filenames using `--pattern`
- sort files
- process sequentially
- skip output if expected output already exists and is non-empty
- if output exists but is empty, remove it and retry
- if one transcode fails:
  - remove partial output
  - continue to next input
- print batch summary:
  - successful
  - skipped
  - failed
- return nonzero exit status if any file failed

Sequential processing is intentional:
- do not run multiple VAAPI/DNxHR jobs concurrently by default

---

## Desired single-file behavior

If input is one file:
- process it directly
- if output already exists, treat as an error rather than silently skipping
- if transcode fails, delete partial output
- Ctrl+C should delete partial output and exit cleanly

---

## Current codec routing

### HEVC source

For HQX:
- Intel VAAPI hardware decode
- hardware frame output: VAAPI
- download as Y210
- convert to planar 10-bit 4:2:2
- encode DNxHR HQX in software

For proxy:
- Intel VAAPI hardware decode
- Intel VAAPI scale to 1080p NV12
- download 1080p NV12
- convert to yuv422p
- encode DNxHR SQ in software

### H.264 source

For now:
- software decode
- software scaling for proxy
- DNxHR software encode

This is intentional because the user's Sony H.264 4:2:2 10-bit files did not go through the same working VAAPI pipeline.

---

## Expected ffmpeg commands

### HEVC HQX

```bash
ffmpeg \
  -nostdin \
  -hwaccel vaapi \
  -hwaccel_device /dev/dri/renderD129 \
  -hwaccel_output_format vaapi \
  -extra_hw_frames 128 \
  -i "$INPUT" \
  -map 0:v:0 \
  -map 0:a? \
  -vf 'hwdownload,format=y210le,format=yuv422p10le' \
  -c:v dnxhd \
  -profile:v dnxhr_hqx \
  -pix_fmt yuv422p10le \
  -color_range pc \
  -c:a copy \
  -timecode "$OUTPUT_TIMECODE" \
  "$OUTPUT"
```

Add `-timecode "$OUTPUT_TIMECODE"` when the Python source-timecode resolver returns a value. That includes Sony LTC parsed from XML/`NonRealTimeMeta` and embedded metadata timecode such as MOV/MP4 `tmcd`.

### H.264 HQX

```bash
ffmpeg \
  -nostdin \
  -i "$INPUT" \
  -map 0:v:0 \
  -map 0:a? \
  -c:v dnxhd \
  -profile:v dnxhr_hqx \
  -pix_fmt yuv422p10le \
  -color_range pc \
  -c:a copy \
  -timecode "$OUTPUT_TIMECODE" \
  "$OUTPUT"
```

### HEVC proxy

```bash
ffmpeg \
  -nostdin \
  -hwaccel vaapi \
  -hwaccel_device /dev/dri/renderD129 \
  -hwaccel_output_format vaapi \
  -extra_hw_frames 128 \
  -i "$INPUT" \
  -map 0:v:0 \
  -map 0:a? \
  -map_metadata 0 \
  -vf 'scale_vaapi=w=1920:h=1080:format=nv12,hwdownload,format=nv12,format=yuv422p' \
  -c:v dnxhd \
  -profile:v dnxhr_sq \
  -pix_fmt yuv422p \
  -color_range pc \
  -c:a copy \
  -timecode "$OUTPUT_TIMECODE" \
  "$OUTPUT"
```

### H.264 proxy

```bash
ffmpeg \
  -nostdin \
  -i "$INPUT" \
  -map 0:v:0 \
  -map 0:a? \
  -map_metadata 0 \
  -vf 'scale=1920:-2,format=yuv422p' \
  -c:v dnxhd \
  -profile:v dnxhr_sq \
  -pix_fmt yuv422p \
  -color_range pc \
  -c:a copy \
  -timecode "$OUTPUT_TIMECODE" \
  "$OUTPUT"
```

Again, include `-timecode` only when the Python source-timecode resolver returns a value. Do not synthesize a value when no metadata timecode exists.

---

## Current output naming

HQX:

```text
C2787_MEZZANINE.mov
```

Proxy:

```text
C2787_PROXY.mov
```

These names are also used to decide whether directory processing should skip a clip.

---

## Resolve proxy requirements

For Resolve `Link Proxy Media`, proxy should match the original on:
- frame rate
- frame count / duration
- source timecode range
- DF/NDF mode

Resolution can differ.

A correct 60p DF proxy should appear in Resolve as:

```text
59.940 DF
```

not merely:

```text
59.940
```

FilmRefit also includes a self-contained DaVinci Resolve Lua utility script:

```text
resolve_importer/FilmRefit Proxy Linker.lua
```

The script scans all bins in the current Resolve project's media pool and links existing proxies with Resolve's `LinkProxyMedia` scripting API. It does not create proxies or inspect media essence.

Proxy discovery is intentionally deterministic. For a source clip:

```text
/path/to/Clip001.mov
```

the script checks sibling files named:

```text
/path/to/Clip001_PROXY.mov
/path/to/Clip001_PROXY.MOV
/path/to/Clip001_PROXY.mp4
/path/to/Clip001_PROXY.MP4
/path/to/Clip001_PROXY.mxf
/path/to/Clip001_PROXY.MXF
```

The Avalonia app exposes installation from its Settings screen. The installer chooses the first existing per-user Resolve scripts directory from known platform candidates, or falls back to the standard default for that OS. The current Windows candidates include:

```text
%APPDATA%\Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility
%USERPROFILE%\AppData\Roaming\Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility
```

---

## Future / likely enhancements

### 1. Robust DF/NDF verification

This is the most important near-term area.

Potential improvements:
- verify Sony `LtcChangeTable` parsing against multiple known DF and NDF clips
- compare parsed TC against ffprobe textual timecode
- optionally inspect MP4 binary RTMD if XML is missing
- provide a diagnostic mode such as:

```bash
./video-to-dnxhr.py --inspect C2787.MP4
```

that prints:
- codec
- fps
- pix_fmt
- textual ffprobe TC
- Sony parsed TC
- DF/NDF
- tcFps
- halfStep
- XML source

### 2. Repair existing proxies without re-encoding

Useful for already-generated proxies with wrong DF/NDF.

Possible mode:

```bash
--repair-timecode
```

Behavior:
- detect source clip from proxy stem
- read correct source TC
- stream-copy proxy video/audio
- write corrected MOV `tmcd`
- replace or write `_FIXED.mov`

### 3. Used-timeline-media transcoding

Longer-term workflow goal:
- edit with proxies
- export Resolve XML/FCPXML/EDL
- parse used source ranges
- add handles, e.g. 3 seconds
- transcode only used ranges to DNxHR HQX
- preserve / adjust source timecode so Resolve can conform correctly
- optionally rewrite XML to point directly at generated HQX segments

This would be a custom hardware-decoded alternative to Resolve Media Management.

### 4. More generic camera support

The script is becoming camera-neutral except for Sony metadata parsing.

Desired behavior for other cameras:
- generic ffprobe probing
- if a standard MOV `tmcd` timecode track exists, preserve it
- only use Sony-specific parser when Sony metadata is present
- do not assume Sony filename patterns if user supplies `--pattern`

### 5. Better validation

After successful encode, validate generated proxies and mezzanines before reporting success in the Avalonia wrapper. Probe the output through the Python probe interface and compare its duration with the original clip duration. Allow up to one second of difference because container duration metadata can differ in the hundredths-of-a-second range, especially on 59.94/60 fps footage.

If duration validation fails:
- mark the per-file Status row as failed
- keep the overall batch progress bar in its error state
- report the original duration, output duration, and difference when available

Future validation can also check:
- expected codec
- expected resolution
- expected frame rate
- expected pixel format
- expected timecode
- optionally delete output

### 6. Logging

Could add:
- per-file log files
- JSON batch report
- elapsed time
- average FPS
- encoded size

### 7. Configurability

Potential CLI options:
- `--vaapi-device /dev/dri/renderD129`
- `--extra-hw-frames 128`
- `--proxy-width 1920`
- `--proxy-profile sq|lb`
- `--overwrite`
- `--skip-existing`
- `--recursive`
- `--dry-run`

Defaults should preserve the current known-working setup.

---

## User preferences / guardrails

- Max quality matters for HQX.
- Do not silently reduce 10-bit 4:2:2 HQX to 8-bit/4:2:0.
- Proxy quality can be reduced because proxies are editorial-only.
- Keep originals forever.
- Do not guess standardized color metadata.
- Do not guess DF/NDF from frame rate alone.
- Do not use `/dev/dri/renderD128`.
- Prefer concrete, direct behavior over hidden magic.
- Scripts should fail safely and remove partial outputs.
- Batch processing should continue after individual failures.
- User is comfortable with Python and FFmpeg.

---

## Current observed sample files

### C2782
- HEVC Rext
- 3840x2160
- 23.976p
- yuv422p10le
- full range (`pc`)

### C2787
- HEVC Rext
- 3840x2160
- 59.94p
- yuv422p10le
- source timecode text:
  - `05:48:34:10`
- Resolve recognizes source as:
  - `59.940 DF`
- correct proxy MOV timecode form:
  - `05:48:34;10`

---

## Summary

The main engineering problem is not basic transcoding. It is building a robust Linux post-production pipeline that:

1. uses Intel VAAPI for Sony HEVC 4:2:2 10-bit decoding;
2. creates either:
   - full-quality DNxHR HQX intermediates, or
   - 1080p DNxHR SQ editorial proxies;
3. preserves Resolve-compatible source timecode, including DF/NDF semantics;
4. handles long-running VAAPI/FFmpeg failures safely;
5. supports both files and directories;
6. is extensible to other cameras without hard-coding Sony assumptions everywhere.

The current Python script is the right direction. The highest-priority technical work is validating and hardening the Sony DF/NDF metadata parser and output timecode verification.
