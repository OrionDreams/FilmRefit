# DaVinci Resolve proxy linker

`FilmRefit Proxy Linker.lua` runs inside DaVinci Resolve and links already-created FilmRefit proxies for every clip in the current project's media pool.

## Install

Copy `FilmRefit Proxy Linker.lua` into Resolve's per-user `Fusion/Scripts/Utility` directory, then restart Resolve.

Common locations:

```text
Linux:   ~/.local/share/DaVinciResolve/Fusion/Scripts/Utility/
macOS:   ~/Library/Application Support/Blackmagic Design/DaVinci Resolve/Fusion/Scripts/Utility/
Windows: %APPDATA%\Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility\
```

FilmRefit's app can install the script into that folder from the Resolve integration settings.

## Run

1. Open the Resolve project containing your source media.
2. Choose **Workspace -> Scripts -> FilmRefit Proxy Linker**.
3. The script scans all media pool bins and tries to link a proxy next to each original source file.

The proxy naming convention is:

```text
Source: /path/to/Clip001.mov
Proxy:  /path/to/Clip001_PROXY.mov
```

The script checks these proxy extensions, in order:

```text
mov, MOV, mp4, MP4, mxf, MXF
```

The script does not create proxies. It only links existing proxy files using Resolve's `LinkProxyMedia` scripting API.
