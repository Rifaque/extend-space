#!/bin/sh
# Removes ExtendSpace from your home folder (D102). Your pairing (identity and trusted tablets) in
# ~/.local/share/ExtendSpace is kept unless you pass --purge.
set -eu
PREFIX=${EXTENDSPACE_PREFIX:-$HOME/.local}
DATA=${XDG_DATA_HOME:-$HOME/.local/share}
rm -f "$PREFIX/bin/extendspace" "$DATA/applications/extendspace.desktop" "$DATA/icons/hicolor/scalable/apps/extendspace.svg"
rm -rf "$PREFIX/opt/extendspace"
if [ "${1:-}" = "--purge" ]; then
    rm -rf "$DATA/ExtendSpace"
    echo "ExtendSpace and its data were removed."
else
    echo "ExtendSpace was removed. Your pairing data stays in $DATA/ExtendSpace (run with --purge to remove it)."
fi
