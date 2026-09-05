#!/usr/bin/env python3
"""Create a PNG-backed macOS .icns file from an .iconset directory."""

from __future__ import annotations

import struct
import sys
from pathlib import Path


PNG_TO_ICNS_TYPES = (
    ("icon_16x16.png", "icp4"),
    ("icon_16x16@2x.png", "ic11"),
    ("icon_32x32.png", "icp5"),
    ("icon_32x32@2x.png", "ic12"),
    ("icon_128x128.png", "ic07"),
    ("icon_128x128@2x.png", "ic13"),
    ("icon_256x256.png", "ic08"),
    ("icon_256x256@2x.png", "ic14"),
    ("icon_512x512.png", "ic09"),
    ("icon_512x512@2x.png", "ic10"),
)


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: png-iconset-to-icns.py INPUT.iconset OUTPUT.icns", file=sys.stderr)
        return 2

    iconset_dir = Path(sys.argv[1])
    output_path = Path(sys.argv[2])
    chunks = []

    for file_name, icon_type in PNG_TO_ICNS_TYPES:
        png_path = iconset_dir / file_name
        if not png_path.is_file():
            print(f"Missing iconset PNG: {png_path}", file=sys.stderr)
            return 1

        png_data = png_path.read_bytes()
        if not png_data.startswith(b"\x89PNG\r\n\x1a\n"):
            print(f"Not a PNG file: {png_path}", file=sys.stderr)
            return 1

        chunk_length = 8 + len(png_data)
        chunks.append(icon_type.encode("ascii") + struct.pack(">I", chunk_length) + png_data)

    icon_data = b"".join(chunks)
    output_path.write_bytes(b"icns" + struct.pack(">I", 8 + len(icon_data)) + icon_data)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
