#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
source_icon="${1:-$repo_root/local-media/FilmRefit.png}"

if ! command -v magick >/dev/null 2>&1; then
  echo "ImageMagick 'magick' is required to generate FilmRefit icons." >&2
  exit 1
fi

if [[ ! -f "$source_icon" ]]; then
  echo "Source icon not found: $source_icon" >&2
  exit 1
fi

app_icon_dir="$repo_root/src/FilmRefit.App/Assets/Icons"
linux_icon_dir="$repo_root/packaging/linux"
macos_iconset_dir="$repo_root/packaging/macos/FilmRefit.iconset"
web_icon_dir="$repo_root/packaging/web"

mkdir -p "$app_icon_dir" \
  "$macos_iconset_dir" \
  "$web_icon_dir" \
  "$linux_icon_dir/hicolor/16x16/apps" \
  "$linux_icon_dir/hicolor/24x24/apps" \
  "$linux_icon_dir/hicolor/32x32/apps" \
  "$linux_icon_dir/hicolor/48x48/apps" \
  "$linux_icon_dir/hicolor/64x64/apps" \
  "$linux_icon_dir/hicolor/128x128/apps" \
  "$linux_icon_dir/hicolor/256x256/apps" \
  "$linux_icon_dir/hicolor/512x512/apps" \
  "$linux_icon_dir/AppDir"

for size in 16 24 32 48 64 128 256 512; do
  magick "$source_icon" -resize "${size}x${size}" "$app_icon_dir/filmrefit-$size.png"
  cp "$app_icon_dir/filmrefit-$size.png" "$linux_icon_dir/hicolor/${size}x${size}/apps/filmrefit.png"
done

cp "$source_icon" "$app_icon_dir/FilmRefit.png"
cp "$app_icon_dir/filmrefit-512.png" "$linux_icon_dir/filmrefit.png"
cp "$linux_icon_dir/filmrefit.desktop" "$linux_icon_dir/AppDir/filmrefit.desktop"
cp "$linux_icon_dir/filmrefit.png" "$linux_icon_dir/AppDir/filmrefit.png"
cp "$linux_icon_dir/filmrefit.png" "$linux_icon_dir/AppDir/.DirIcon"

magick "$source_icon" -define icon:auto-resize=256,128,64,48,32,24,16 "$app_icon_dir/FilmRefit.ico"
magick "$source_icon" -define icon:auto-resize=64,48,32,16 "$web_icon_dir/favicon.ico"

magick "$source_icon" -resize 16x16 "$web_icon_dir/favicon-16.png"
magick "$source_icon" -resize 32x32 "$web_icon_dir/favicon-32.png"
magick "$source_icon" -resize 48x48 "$web_icon_dir/favicon-48.png"
magick "$source_icon" -resize 180x180 "$web_icon_dir/apple-touch-icon.png"
magick "$source_icon" -resize 192x192 "$web_icon_dir/icon-192.png"
magick "$source_icon" -resize 512x512 "$web_icon_dir/icon-512.png"

magick "$source_icon" -resize 16x16 "$macos_iconset_dir/icon_16x16.png"
magick "$source_icon" -resize 32x32 "$macos_iconset_dir/icon_16x16@2x.png"
magick "$source_icon" -resize 32x32 "$macos_iconset_dir/icon_32x32.png"
magick "$source_icon" -resize 64x64 "$macos_iconset_dir/icon_32x32@2x.png"
magick "$source_icon" -resize 128x128 "$macos_iconset_dir/icon_128x128.png"
magick "$source_icon" -resize 256x256 "$macos_iconset_dir/icon_128x128@2x.png"
magick "$source_icon" -resize 256x256 "$macos_iconset_dir/icon_256x256.png"
magick "$source_icon" -resize 512x512 "$macos_iconset_dir/icon_256x256@2x.png"
magick "$source_icon" -resize 512x512 "$macos_iconset_dir/icon_512x512.png"
magick "$source_icon" -resize 1024x1024 "$macos_iconset_dir/icon_512x512@2x.png"

if command -v iconutil >/dev/null 2>&1; then
  iconutil -c icns "$macos_iconset_dir" -o "$repo_root/packaging/macos/FilmRefit.icns"
else
  "$script_dir/png-iconset-to-icns.py" "$macos_iconset_dir" "$repo_root/packaging/macos/FilmRefit.icns"
fi
