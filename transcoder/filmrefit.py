#!/usr/bin/env python3

import argparse
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Optional


VAAPI_DEVICE = "/dev/dri/renderD129"
EXTRA_HW_FRAMES = "128"
FFMPEG_STATS_PERIOD_SECONDS = "5"
__version__ = "0.1.0"


def resolve_runtime_tool(name: str) -> str:
    env_name = f"FILMREFIT_{name.upper()}"
    configured = os.environ.get(env_name)
    if configured:
        return configured

    executable = f"{name}.exe" if sys.platform == "win32" else name
    candidates = []

    if getattr(sys, "frozen", False):
        executable_path = Path(sys.executable).resolve()
        candidates.append(executable_path.parent.parent / "ffmpeg" / executable)

    script_root = Path(__file__).resolve().parent.parent
    candidates.append(script_root / "runtime" / "ffmpeg" / executable)

    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)

    return shutil.which(executable) or executable


FFMPEG = resolve_runtime_tool("ffmpeg")
FFPROBE = resolve_runtime_tool("ffprobe")


# ---------------------------------------------------------------------
# General helpers
# ---------------------------------------------------------------------

def die(message: str, code: int = 1) -> None:
    print(f"Error: {message}", file=sys.stderr)
    raise SystemExit(code)


def run_capture(cmd: list[str]) -> str:
    try:
        result = subprocess.run(
            cmd,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        return result.stdout

    except FileNotFoundError:
        die(f"command not found: {cmd[0]}")

    except subprocess.CalledProcessError as exc:
        if exc.stderr:
            print(exc.stderr, file=sys.stderr)

        die(f"command failed: {' '.join(cmd)}")

    raise RuntimeError("unreachable")


def format_command(cmd: list[str]) -> str:
    return " ".join(shlex.quote(arg) for arg in cmd)


# ---------------------------------------------------------------------
# ffprobe
# ---------------------------------------------------------------------

def probe_file(path: Path) -> dict:
    output = run_capture([
        FFPROBE,
        "-v", "error",
        "-show_streams",
        "-show_format",
        "-of", "json",
        str(path),
    ])

    data = json.loads(output)

    video = None
    audio = None
    data_streams = []
    has_sony_rtmd = False

    for stream in data.get("streams", []):
        codec_type = stream.get("codec_type")
        disposition = stream.get("disposition", {})
        codec_tag = stream.get("codec_tag_string")

        if (
            codec_type == "video"
            and video is None
            and disposition.get("attached_pic") != 1
        ):
            video = stream

        elif codec_type == "audio" and audio is None:
            audio = stream

        elif codec_type == "data":
            data_streams.append(stream)
            if codec_tag == "rtmd":
                has_sony_rtmd = True

    if video is None:
        raise RuntimeError("no video stream found")

    timecode = None

    # Sony usually stores TC on its RTMD data stream.
    for stream in data_streams:
        tc = stream.get("tags", {}).get("timecode")
        if tc:
            timecode = tc
            break

    # Fallbacks for other containers/cameras.
    if not timecode:
        timecode = video.get("tags", {}).get("timecode")

    if not timecode:
        for stream in data.get("streams", []):
            tc = stream.get("tags", {}).get("timecode")
            if tc:
                timecode = tc
                break

    if not timecode:
        timecode = data.get("format", {}).get("tags", {}).get("timecode")

    return {
        "codec": video.get("codec_name"),
        "audio_codec": audio.get("codec_name") if audio else None,
        "fps": video.get("r_frame_rate"),
        "avg_fps": video.get("avg_frame_rate"),
        "video_frames": video.get("nb_frames"),
        "pix_fmt": video.get("pix_fmt"),
        "bits_per_raw_sample": video.get("bits_per_raw_sample"),
        "color_space": video.get("color_space"),
        "color_transfer": video.get("color_transfer"),
        "width": video.get("width"),
        "height": video.get("height"),
        "duration": data.get("format", {}).get("duration"),
        "timecode": timecode,
        "has_audio": audio is not None,
        "has_sony_rtmd": has_sony_rtmd,
        "format_brand": data.get("format", {}).get("tags", {}).get("major_brand"),
        "compatible_brands": data.get("format", {}).get("tags", {}).get("compatible_brands"),
        "camera": camera_from_format_tags(data.get("format", {}).get("tags", {})),
        "lens": read_tag(
            data.get("format", {}).get("tags", {}),
            "com.apple.quicktime.lens",
        ) or read_tag(data.get("format", {}).get("tags", {}), "lens"),
    }


def read_tag(tags: dict, tag_name: str) -> Optional[str]:
    for key, value in tags.items():
        if key.lower() == tag_name.lower():
            text = str(value).strip()
            return text or None

    return None


def camera_from_format_tags(tags: dict) -> Optional[str]:
    camera = (
        read_tag(tags, "com.apple.quicktime.make")
        or read_tag(tags, "make")
    )

    if camera:
        return camera

    encoder = read_tag(tags, "encoder")

    if encoder and encoder.lower().startswith("dji "):
        return encoder

    return None


def should_parse_sony_timecode(path: Path, probe: dict) -> bool:
    if find_sidecar_xml(path):
        return True

    if probe.get("has_sony_rtmd"):
        return True

    brands = " ".join(
        str(value)
        for value in (
            probe.get("format_brand"),
            probe.get("compatible_brands"),
        )
        if value
    ).lower()

    return "xavc" in brands or "nras" in brands


def resolve_source_timecode(path: Path, probe: dict) -> dict:
    sony_tc = parse_sony_timecode(path) if should_parse_sony_timecode(path, probe) else None

    timecode = None
    timecode_source = None
    drop_frame = None
    tc_fps = None
    half_step = None

    if sony_tc:
        timecode = sony_tc["timecode"]
        timecode_source = sony_tc["source"]
        drop_frame = sony_tc["drop_frame"]
        tc_fps = sony_tc["tc_fps"]
        half_step = sony_tc["half_step"]

    elif probe["timecode"]:
        timecode = probe["timecode"]
        timecode_source = "embedded ffprobe tag"

    return {
        "timecode": timecode,
        "timecode_source": timecode_source,
        "drop_frame": drop_frame,
        "tc_fps": tc_fps,
        "half_step": half_step,
    }


def probe_file_for_ui(path: Path) -> dict:
    probe = probe_file(path)
    source_timecode = resolve_source_timecode(path, probe)

    return {
        **probe,
        **source_timecode,
    }


# ---------------------------------------------------------------------
# Sony NonRealTimeMeta / LTC parsing
# ---------------------------------------------------------------------

def find_sidecar_xml(path: Path) -> Optional[Path]:
    """
    Sony commonly creates:

        C2787.MP4
        C2787M01.XML
    """

    candidate = path.with_name(f"{path.stem}M01.XML")

    if candidate.is_file():
        return candidate

    return None


def find_embedded_sony_xml(path: Path) -> Optional[bytes]:
    """
    Search the tail of the MP4 for Sony's embedded NonRealTimeMeta XML.

    We deliberately avoid loading multi-GB files into memory.
    """

    end_marker = b"</NonRealTimeMeta>"

    # Sony metadata is normally near the end of the MP4.
    search_sizes = [
        4 * 1024 * 1024,
        16 * 1024 * 1024,
        64 * 1024 * 1024,
    ]

    file_size = path.stat().st_size

    with path.open("rb") as file:
        for requested_size in search_sizes:
            read_size = min(requested_size, file_size)

            file.seek(file_size - read_size)
            tail = file.read(read_size)

            end = tail.rfind(end_marker)
            if end == -1:
                continue

            # Find the beginning of the enclosing XML document.
            # Namespace/preamble can vary, so look for XML declaration
            # first and then fall back to the NonRealTimeMeta tag.
            start = tail.rfind(
                b'<?xml version="1.0"',
                0,
                end,
            )

            if start == -1:
                candidates = [
                    tail.rfind(b"<NonRealTimeMeta", 0, end),
                    tail.rfind(b":NonRealTimeMeta", 0, end),
                ]

                start = max(candidates)

            if start == -1:
                continue

            end += len(end_marker)

            return tail[start:end]

    return None


def load_sony_xml(path: Path) -> tuple[Optional[bytes], Optional[str]]:
    sidecar = find_sidecar_xml(path)

    if sidecar:
        try:
            return sidecar.read_bytes(), f"sidecar {sidecar.name}"
        except OSError as exc:
            print(
                f"Warning: unable to read Sony sidecar {sidecar}: {exc}",
                file=sys.stderr,
            )

    embedded = find_embedded_sony_xml(path)

    if embedded:
        return embedded, "embedded NonRealTimeMeta"

    return None, None


def local_name(tag: str) -> str:
    """
    Strip XML namespace.

    Example:

        {urn:schemas-professionalDisc:nonRealTimeMeta:ver.2.20}
        LtcChangeTable

    becomes:

        LtcChangeTable
    """

    return tag.rsplit("}", 1)[-1]


def decode_sony_ltc_value(value: str) -> tuple[str, bool]:
    """
    Decode Sony's LtcChange value.

    Sony stores timecode in an FFSSMMHH-style numeric representation,
    with the drop-frame flag embedded in the frame tens nibble.

    Returns:

        ("05:48:34:10", False)

    or:

        ("05:48:34;10", True)
    """

    value = value.strip()

    if not re.fullmatch(r"\d{8}", value):
        raise ValueError(
            f"unexpected Sony LTC value {value!r}; expected 8 digits"
        )

    first_digit = int(value[0])

    # Drop-frame flag is encoded by adding 4 to the frame tens digit.
    drop_frame = first_digit >= 4

    if drop_frame:
        first_digit -= 4

        if first_digit < 0 or first_digit > 2:
            raise ValueError(
                f"invalid Sony DF frame nibble in LTC value {value!r}"
            )

        value = str(first_digit) + value[1:]

    ff = value[0:2]
    ss = value[2:4]
    mm = value[4:6]
    hh = value[6:8]

    separator = ";" if drop_frame else ":"

    return f"{hh}:{mm}:{ss}{separator}{ff}", drop_frame


def parse_timecode_components(timecode: str) -> Optional[tuple[int, int, int, int]]:
    match = re.fullmatch(r"(\d{2}):(\d{2}):(\d{2})[:;](\d{2})", timecode.strip())
    if not match:
        return None

    return tuple(int(part) for part in match.groups())


def expand_half_step_timecode(timecode: str) -> str:
    components = parse_timecode_components(timecode)
    if components is None:
        return timecode

    hh, mm, ss, ff = components
    separator = ";" if ";" in timecode else ":"
    return f"{hh:02d}:{mm:02d}:{ss:02d}{separator}{ff * 2:02d}"


def timecodes_equivalent(probe_timecode: str, sony_timecode: str, half_step: Optional[str]) -> bool:
    probe = parse_timecode_components(probe_timecode)
    sony = parse_timecode_components(sony_timecode)

    if probe is None or sony is None:
        return probe_timecode.replace(";", ":") == sony_timecode.replace(";", ":")

    if probe[:3] != sony[:3]:
        return False

    if probe[3] == sony[3]:
        return True

    return half_step == "true" and probe[3] == sony[3] * 2


def parse_sony_timecode(path: Path) -> Optional[dict]:
    """
    Parse Sony LTC metadata to get the actual DF/NDF state rather than
    guessing from the frame rate.
    """

    xml_bytes, source = load_sony_xml(path)

    if xml_bytes is None:
        return None

    try:
        root = ET.fromstring(xml_bytes)

    except ET.ParseError as exc:
        print(
            f"Warning: Sony metadata XML could not be parsed: {exc}",
            file=sys.stderr,
        )
        return None

    ltc_table = None

    for element in root.iter():
        if local_name(element.tag) == "LtcChangeTable":
            ltc_table = element
            break

    if ltc_table is None:
        return None

    tc_fps = ltc_table.attrib.get("tcFps")
    half_step = ltc_table.attrib.get("halfStep")

    start_value = None

    for element in ltc_table.iter():
        if local_name(element.tag) != "LtcChange":
            continue

        # The first increment entry represents the running TC start.
        status = element.attrib.get("status")

        if status == "increment":
            start_value = element.attrib.get("value")
            if start_value:
                break

    if not start_value:
        return None

    try:
        tc_string, drop_frame = decode_sony_ltc_value(start_value)

    except ValueError as exc:
        print(f"Warning: {exc}", file=sys.stderr)
        return None

    if half_step == "true":
        tc_string = expand_half_step_timecode(tc_string)

    return {
        "timecode": tc_string,
        "drop_frame": drop_frame,
        "tc_fps": tc_fps,
        "half_step": half_step,
        "raw_value": start_value,
        "source": source,
    }


# ---------------------------------------------------------------------
# Output naming
# ---------------------------------------------------------------------

def build_output_path(input_path: Path, mode: str) -> Path:
    if mode == "hqx":
        return input_path.with_name(
            f"{input_path.stem}_MEZZANINE.mov"
        )

    if mode == "proxy":
        return input_path.with_name(
            f"{input_path.stem}_PROXY.mov"
        )

    raise ValueError(f"unsupported mode: {mode}")


def build_suffixed_output_path(output_path: Path, suffix: str) -> Path:
    suffixed_path = output_path.with_name(
        f"{output_path.stem}{suffix}{output_path.suffix}"
    )

    if not suffixed_path.exists():
        return suffixed_path

    for index in range(1, 1000):
        candidate = output_path.with_name(
            f"{output_path.stem}{suffix}_{index}{output_path.suffix}"
        )
        if not candidate.exists():
            return candidate

    raise RuntimeError(f"too many suffixed outputs exist for {output_path}")


def build_failed_output_path(output_path: Path) -> Path:
    return build_suffixed_output_path(output_path, "_FAILED")


def build_timecode_fixed_output_path(output_path: Path) -> Path:
    return build_suffixed_output_path(output_path, "_TCFIX")


def build_bad_timecode_output_path(output_path: Path) -> Path:
    return build_suffixed_output_path(output_path, "_BAD_TC")


def preserve_failed_output(output_path: Path) -> None:
    if not output_path.exists():
        return

    failed_path = build_failed_output_path(output_path)
    print(f"Preserving failed output: {failed_path}")

    try:
        output_path.rename(failed_path)

    except OSError as exc:
        print(
            f"Warning: unable to preserve failed output: {exc}",
            file=sys.stderr,
        )


def ensure_directory_writable(directory: Path) -> bool:
    try:
        with tempfile.NamedTemporaryFile(
            prefix=".filmrefit-write-test-",
            dir=directory,
            delete=False,
        ) as temp_file:
            temp_path = Path(temp_file.name)

        temp_path.unlink()
        return True

    except OSError as exc:
        print(
            f"Error: cannot write to output directory {directory}: {exc}",
            file=sys.stderr,
        )
        return False


def resolve_source_for_output(output_path: Path) -> Path:
    stem = output_path.stem
    source_stem = None

    for suffix in ("_PROXY", "_MEZZANINE"):
        if stem.endswith(suffix):
            source_stem = stem[:-len(suffix)]
            break

    if not source_stem:
        raise RuntimeError(
            "repair input must be a FilmRefit proxy or mezzanine file"
        )

    for extension in (".MP4", ".mp4", ".MOV", ".mov", ".MXF", ".mxf"):
        candidate = output_path.with_name(f"{source_stem}{extension}")
        if candidate.exists():
            return candidate

    raise RuntimeError(f"source clip not found next to {output_path}")


# ---------------------------------------------------------------------
# FFmpeg command construction
# ---------------------------------------------------------------------

def build_ffmpeg_command(
    input_path: Path,
    output_path: Path,
    codec: str,
    mode: str,
    timecode: Optional[str],
    camera: Optional[str] = None,
) -> list[str]:

    cmd = [
        FFMPEG,
        "-nostdin",
        "-stats_period", FFMPEG_STATS_PERIOD_SECONDS,
    ]

    # -------------------------------------------------------------
    # Decode
    # -------------------------------------------------------------

    if codec == "hevc":
        cmd += [
            "-hwaccel", "vaapi",
            "-hwaccel_device", VAAPI_DEVICE,
            "-hwaccel_output_format", "vaapi",
            "-extra_hw_frames", EXTRA_HW_FRAMES,
        ]

    cmd += [
        "-i", str(input_path),
        "-map", "0:v:0",
        "-map", "0:a?",
    ]

    # -------------------------------------------------------------
    # HQX
    # -------------------------------------------------------------

    if mode == "hqx":

        if codec == "hevc":
            cmd += [
                "-vf",
                "hwdownload,format=y210le,format=yuv422p10le",
            ]

        cmd += [
            "-c:v", "dnxhd",
            "-profile:v", "dnxhr_hqx",
            "-pix_fmt", "yuv422p10le",
            "-color_range", "pc",
            "-c:a", "copy",
        ]

    # -------------------------------------------------------------
    # Proxy
    # -------------------------------------------------------------

    elif mode == "proxy":

        cmd += [
            "-map_metadata", "0",
        ]

        if codec == "hevc":
            cmd += [
                "-vf",
                (
                    "scale_vaapi="
                    "w=1920:h=1080:format=nv12,"
                    "hwdownload,"
                    "format=nv12,"
                    "format=yuv422p"
                ),
            ]

        elif codec == "h264":
            cmd += [
                "-vf",
                "scale=1920:-2,format=yuv422p",
            ]

        cmd += [
            "-c:v", "dnxhd",
            "-profile:v", "dnxhr_sq",
            "-pix_fmt", "yuv422p",
            "-color_range", "pc",
            "-c:a", "copy",
        ]

    else:
        raise ValueError(f"unsupported mode: {mode}")

    # -------------------------------------------------------------
    # Timecode
    # -------------------------------------------------------------

    if timecode:
        cmd += [
            "-timecode", timecode,
        ]

    if camera:
        cmd += [
            "-movflags", "use_metadata_tags",
            "-metadata", f"com.apple.quicktime.make={camera}",
        ]

    cmd.append(str(output_path))

    return cmd


def build_timecode_repair_command(
    input_path: Path,
    output_path: Path,
    timecode: str,
) -> list[str]:
    return [
        FFMPEG,
        "-nostdin",
        "-y",
        "-i", str(input_path),
        "-map", "0:v:0",
        "-map", "0:a?",
        "-map_metadata", "0",
        "-c", "copy",
        "-timecode", timecode,
        str(output_path),
    ]


# ---------------------------------------------------------------------
# FFmpeg execution
# ---------------------------------------------------------------------

def run_ffmpeg(cmd: list[str], output_path: Path) -> bool:
    print()
    print("Running:")
    print(format_command(cmd))
    print()

    try:
        result = subprocess.run(cmd)

    except KeyboardInterrupt:
        print("\nInterrupted.")

        if output_path.exists():
            preserve_failed_output(output_path)

        raise

    if result.returncode == 0:
        return True

    print()
    print("ERROR: transcode failed.")

    if output_path.exists():
        preserve_failed_output(output_path)

    return False


# ---------------------------------------------------------------------
# Per-file processing
# ---------------------------------------------------------------------

def process_file(
    input_path: Path,
    mode: str,
    skip_existing: bool,
) -> str:
    """
    Returns one of:

        success
        skipped
        failed
    """

    output_path = build_output_path(input_path, mode)

    # -------------------------------------------------------------
    # Existing output
    # -------------------------------------------------------------

    if output_path.exists():

        try:
            size = output_path.stat().st_size

        except OSError:
            size = 0

        if size > 0:

            if skip_existing:
                print(f"Skipping: {input_path.name}")
                print(f"Output already exists: {output_path}")
                return "skipped"

            print(
                f"Error: output already exists: {output_path}",
                file=sys.stderr,
            )
            return "failed"

        print(f"Removing empty output: {output_path}")

        try:
            output_path.unlink()

        except OSError as exc:
            print(
                f"Error: unable to remove empty output: {exc}",
                file=sys.stderr,
            )
            return "failed"

    # -------------------------------------------------------------
    # Probe source
    # -------------------------------------------------------------

    try:
        probe = probe_file(input_path)

    except Exception as exc:
        print(
            f"Error probing {input_path}: {exc}",
            file=sys.stderr,
        )
        return "failed"

    codec = probe["codec"]

    if codec not in ("hevc", "h264"):
        print(
            f"Error: unsupported video codec: {codec}",
            file=sys.stderr,
        )
        return "failed"

    # -------------------------------------------------------------
    # Timecode
    # -------------------------------------------------------------

    source_timecode = resolve_source_timecode(input_path, probe)
    output_timecode = source_timecode["timecode"]

    # -------------------------------------------------------------
    # Information
    # -------------------------------------------------------------

    print(f"Input     : {input_path}")
    print(f"Output    : {output_path}")
    print(f"Codec     : {codec}")
    print(f"Pixel fmt : {probe['pix_fmt']}")
    print(f"FPS       : {probe['fps']}")
    print(f"Mode      : {mode}")

    if output_timecode:
        print(f"Timecode  : {output_timecode}")

        if source_timecode["drop_frame"] is not None:
            print(
                "TC format : "
                + ("DF" if source_timecode["drop_frame"] else "NDF")
            )
            print(
                f"TC FPS    : {source_timecode['tc_fps'] or 'unknown'}"
            )
            print(
                f"Half-step : {source_timecode['half_step'] or 'unknown'}"
            )

        else:
            print("TC format : from embedded metadata")

        print(f"TC source : {source_timecode['timecode_source']}")

        if source_timecode["drop_frame"] is not None and probe["timecode"]:
            if not timecodes_equivalent(
                probe["timecode"],
                output_timecode,
                source_timecode["half_step"],
            ):
                print()
                print("WARNING: ffprobe and Sony LTC disagree:")
                print(f"  ffprobe : {probe['timecode']}")
                print(f"  Sony    : {output_timecode}")

    else:
        print("Timecode  : none")

    if codec == "hevc":
        print("Decode    : Intel VAAPI hardware")

    else:
        print("Decode    : software")

    if mode == "proxy":

        if codec == "hevc":
            print("Scaling   : Intel VAAPI hardware")

        else:
            print("Scaling   : software")

        print("Encode    : DNxHR SQ 1920x1080")

    else:
        print("Encode    : DNxHR HQX")

    # -------------------------------------------------------------
    # Build/run command
    # -------------------------------------------------------------

    cmd = build_ffmpeg_command(
        input_path=input_path,
        output_path=output_path,
        codec=codec,
        mode=mode,
        timecode=output_timecode,
        camera=probe.get("camera"),
    )

    try:
        success = run_ffmpeg(cmd, output_path)

    except KeyboardInterrupt:
        raise

    if not success:
        return "failed"

    print()
    print(f"Done: {output_path}")

    return "success"


def repair_timecode(
    output_path: Path,
    replace: bool,
) -> bool:
    source_path = resolve_source_for_output(output_path)
    repaired_path = build_timecode_fixed_output_path(output_path)

    if not ensure_directory_writable(repaired_path.parent):
        return False

    required_bytes = output_path.stat().st_size
    available_bytes = shutil.disk_usage(repaired_path.parent).free

    if available_bytes < required_bytes:
        print(
            "Error: not enough free space to repair timecode: "
            f"need about {format_file_size(required_bytes)}, "
            f"available {format_file_size(available_bytes)}",
            file=sys.stderr,
        )
        return False

    source_probe = probe_file(source_path)
    source_timecode = resolve_source_timecode(source_path, source_probe)
    output_timecode = source_timecode["timecode"]

    if not output_timecode:
        print(f"Error: no source timecode found for {source_path}", file=sys.stderr)
        return False

    print(f"Source    : {source_path}")
    print(f"Input     : {output_path}")
    print(f"Output    : {repaired_path}")
    print(f"Timecode  : {output_timecode}")
    print(f"TC source : {source_timecode['timecode_source']}")

    command = build_timecode_repair_command(
        input_path=output_path,
        output_path=repaired_path,
        timecode=output_timecode,
    )

    success = run_ffmpeg(command, repaired_path)
    if not success:
        return False

    try:
        repaired_probe = probe_file(repaired_path)

    except Exception as exc:
        print(f"Error probing repaired output: {exc}", file=sys.stderr)
        return False

    if repaired_probe["timecode"] != output_timecode:
        print(
            "Error: repaired output timecode mismatch: "
            f"expected {output_timecode}, got {repaired_probe['timecode']}",
            file=sys.stderr,
        )
        return False

    if replace:
        backup_path = build_bad_timecode_output_path(output_path)
        print(f"Backup    : {backup_path}")

        try:
            output_path.rename(backup_path)
            repaired_path.rename(output_path)

        except OSError as exc:
            print(f"Error replacing original output: {exc}", file=sys.stderr)
            return False

        print(f"Replaced  : {output_path}")

    print("Done.")
    return True


def format_file_size(size: int) -> str:
    value = float(size)
    for unit in ("bytes", "KiB", "MiB", "GiB", "TiB"):
        if value < 1024 or unit == "TiB":
            return f"{value:.1f} {unit}" if unit != "bytes" else f"{size} bytes"

        value /= 1024


# ---------------------------------------------------------------------
# Directory handling
# ---------------------------------------------------------------------

def process_directory(
    directory: Path,
    mode: str,
    pattern: re.Pattern,
    pattern_text: str,
) -> int:

    files = sorted(
        path
        for path in directory.iterdir()
        if path.is_file() and pattern.fullmatch(path.name)
    )

    if not files:
        print(
            f"No files matching {pattern_text!r} found in:"
        )
        print(directory)
        return 0

    print(f"Directory : {directory}")
    print(f"Pattern   : {pattern_text}")
    print(f"Mode      : {mode}")
    print(f"Files     : {len(files)}")

    succeeded = 0
    skipped = 0
    failed = 0

    for index, path in enumerate(files, start=1):
        print()
        print("=" * 78)
        print(f"[{index}/{len(files)}] {path.name}")
        print("=" * 78)

        try:
            result = process_file(
                input_path=path,
                mode=mode,
                skip_existing=True,
            )

        except KeyboardInterrupt:
            print()
            print("Batch interrupted.")

            print()
            print("=" * 78)
            print("Partial batch summary")
            print(f"Successful : {succeeded}")
            print(f"Skipped    : {skipped}")
            print(f"Failed     : {failed}")
            print("=" * 78)

            return 130

        if result == "success":
            succeeded += 1

        elif result == "skipped":
            skipped += 1

        else:
            failed += 1

            print()
            print(
                f"Continuing after failure: {path.name}",
                file=sys.stderr,
            )

    print()
    print("=" * 78)
    print("Batch complete")
    print(f"Successful : {succeeded}")
    print(f"Skipped    : {skipped}")
    print(f"Failed     : {failed}")
    print("=" * 78)

    return 1 if failed else 0


# ---------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------

def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Transcode camera footage to DNxHR HQX or "
            "generate DNxHR SQ proxies."
        ),
    )

    parser.add_argument(
        "--mode",
        choices=("hqx", "proxy"),
        default="hqx",
        help="transcode mode (default: hqx)",
    )

    parser.add_argument(
        "--pattern",
        default=r"C\d+\.MP4",
        help=(
            "regex used when input is a directory "
            r"(default: C\d+\.MP4)"
        ),
    )

    parser.add_argument(
        "--probe-json",
        action="store_true",
        help="print source metadata as JSON and exit",
    )

    parser.add_argument(
        "--repair-timecode",
        action="store_true",
        help=(
            "repair a FilmRefit proxy/mezzanine MOV timecode by reading "
            "the matching source clip"
        ),
    )

    parser.add_argument(
        "--replace",
        action="store_true",
        help=(
            "with --repair-timecode, replace the input file after keeping "
            "the original as *_BAD_TC"
        ),
    )

    parser.add_argument(
        "input",
        type=Path,
        help="input video file or directory",
    )

    args = parser.parse_args()

    input_path = args.input.expanduser().resolve()

    try:
        filename_pattern = re.compile(args.pattern)

    except re.error as exc:
        die(f"invalid --pattern regex: {exc}")

    # -------------------------------------------------------------
    # Single file
    # -------------------------------------------------------------

    if input_path.is_file():
        if args.probe_json:
            try:
                print(json.dumps(probe_file_for_ui(input_path)))

            except Exception as exc:
                print(
                    f"Error probing {input_path}: {exc}",
                    file=sys.stderr,
                )
                return 1

            return 0

        if args.repair_timecode:
            try:
                return 0 if repair_timecode(input_path, args.replace) else 1

            except Exception as exc:
                print(f"Error repairing timecode: {exc}", file=sys.stderr)
                return 1

        try:
            result = process_file(
                input_path=input_path,
                mode=args.mode,
                skip_existing=False,
            )

        except KeyboardInterrupt:
            return 130

        return 0 if result == "success" else 1

    # -------------------------------------------------------------
    # Directory
    # -------------------------------------------------------------

    if input_path.is_dir():
        if args.repair_timecode:
            die("--repair-timecode expects a single proxy or mezzanine file")

        return process_directory(
            directory=input_path,
            mode=args.mode,
            pattern=filename_pattern,
            pattern_text=args.pattern,
        )

    die(f"path is neither a file nor directory: {input_path}")

    return 1


if __name__ == "__main__":
    raise SystemExit(main())
