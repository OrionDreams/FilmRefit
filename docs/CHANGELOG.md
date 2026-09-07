# Changelog

All notable changes to FilmRefit will be documented in this file.

## v0.1.1 - (Unreleased)

- Added a DaVinci Resolve Lua proxy linker and app Settings screen controls to install or open the Resolve scripts folder.
- The proxy creation now includes the Camera info metadata in the proxy, if it is found in the original file.
- Fixed Sony 59.94p `halfStep` timecode handling so generated MOV proxies write the full-rate drop-frame timecode value expected by DaVinci Resolve.
- Added stricter output validation that compares video frame counts exactly when available and rejects mismatched proxy/mezzanine outputs.
- Added Python-side output validation so HEVC VAAPI proxy outputs that silently drop frames are rejected and preserved for inspection.
- Changed failed transcodes to preserve partial outputs as `_FAILED` files for inspection instead of deleting them.
- Added proxy/mezzanine timecode repair from the GUI for selected FilmRefit outputs.
- Improved logged FFmpeg command quoting so timecodes containing semicolons can be copied safely into a shell.

## v0.1.0 - 2026-09-06

- Initial GitHub-ready release setup.
- Added Avalonia desktop app packaging for Windows, Linux, and macOS.
- Added Python/FFmpeg transcoder packaging with pinned Python 3.11 release builds.
- Added GPLv3 licensing and third-party license materials.
