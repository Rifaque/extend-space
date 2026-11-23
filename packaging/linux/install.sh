#!/bin/sh
# ExtendSpace for Linux: per-user installer (D102). Needs no root and changes nothing outside your
# home folder. It checks the system packages ExtendSpace uses and tells you how to install missing
# ones; it never installs them itself.
set -eu
HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PREFIX=${EXTENDSPACE_PREFIX:-$HOME/.local}
APP=$PREFIX/opt/extendspace
BIN=$PREFIX/bin
APPS=${XDG_DATA_HOME:-$HOME/.local/share}/applications

if [ "$(id -u)" = "0" ]; then
    echo "Run this as your own user, not as root: ExtendSpace installs into your home folder." >&2
    exit 1
fi

echo "Installing ExtendSpace $(cat "$HERE/VERSION") to $APP"
rm -rf "$APP.new"
mkdir -p "$APP.new" "$BIN" "$APPS"
cp -R "$HERE/app/." "$APP.new/"
chmod 755 "$APP.new/extendspace"
rm -rf "$APP.old"
[ -d "$APP" ] && mv "$APP" "$APP.old"
mv "$APP.new" "$APP"
rm -rf "$APP.old"
cp "$HERE/uninstall.sh" "$APP/uninstall.sh"
chmod 755 "$APP/uninstall.sh"
ln -sf "$APP/extendspace" "$BIN/extendspace"
sed "s|@EXEC@|$APP/extendspace|" "$HERE/extendspace.desktop" > "$APPS/extendspace.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" >/dev/null 2>&1 || true

# Dependency check (read-only).
missing=""
has_element() { command -v gst-inspect-1.0 >/dev/null 2>&1 && gst-inspect-1.0 --exists "$1"; }
command -v gst-inspect-1.0 >/dev/null 2>&1 || missing="$missing gstreamer"
if command -v gst-inspect-1.0 >/dev/null 2>&1; then
    has_element x264enc || has_element vah264enc || has_element vaapih264enc || has_element nvh264enc || missing="$missing h264-encoder"
    has_element ximagesrc || missing="$missing ximagesrc"
    has_element pipewiresrc || missing="$missing pipewiresrc"
fi
command -v adb >/dev/null 2>&1 || missing="$missing adb"

echo "Installed. Start it from your applications menu or run: extendspace"
case ":$PATH:" in *":$BIN:"*) ;; *) echo "Note: $BIN is not on your PATH." ;; esac
if [ -n "$missing" ]; then
    echo
    echo "Missing for full use:$missing"
    echo "Install them with your package manager, for example:"
    echo "  Debian/Ubuntu/Mint: sudo apt install gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-ugly gstreamer1.0-x gstreamer1.0-pipewire adb"
    echo "  Fedora:             sudo dnf install gstreamer1-plugins-good gstreamer1-plugins-ugly-free pipewire-gstreamer android-tools"
    echo "  Arch:               sudo pacman -S gst-plugins-good gst-plugins-ugly gst-plugin-pipewire android-tools"
    echo "adb is needed for USB only; Wi-Fi works without it."
fi
