#!/usr/bin/env bash
# Downloads the free OpenROMs C64 ROM images into src/Tedd.Commodore.Web/wwwroot/roms.
#
# OpenROMs (https://github.com/MEGA65/open-roms, LGPL-3.0) is a clean-room re-implementation of the
# Commodore 64 BASIC, KERNAL and character generator ROMs. The project publishes no GitHub releases;
# the pre-built images live in the "bin/" directory of the repository and are served through
# raw.githubusercontent.com. This script fetches:
#
#     bin/basic_generic.rom    (8192 bytes)  -> roms/basic.bin
#     bin/kernal_generic.rom   (8192 bytes)  -> roms/kernal.bin
#     bin/chargen_openroms.rom (4096 bytes)  -> roms/chargen.bin
#     LICENSE                                 -> roms/LICENSE          (OpenROMs copyright notice, LGPL-3.0)
#     COPYING.LESSER                          -> roms/COPYING.LESSER   (full LGPL-3.0 text)
#
# OpenROMs provides no 1541 DOS ROM, so the browser build boots without a disk drive unless the user
# uploads a real 1541 ROM through the "Load your own ROMs" panel.
#
# The .bin files are git-ignored (see .gitignore); only README.md, LICENSE and COPYING.LESSER are committed.
#
# Usage: tools/fetch-openroms.sh [ref] [destination] [openroms|pxlfont]
#   ref          git ref of the open-roms repository (default: master)
#   destination  target directory (default: <repo>/src/Tedd.Commodore.Web/wwwroot/roms)
#   font         "openroms" (chargen_openroms.rom, default) or "pxlfont" (chargen_pxlfont_2.3.rom)
set -euo pipefail

REF="${1:-master}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="${2:-$SCRIPT_DIR/../src/Tedd.Commodore.Web/wwwroot/roms}"
FONT="${3:-openroms}"

case "$FONT" in
    openroms) CHARGEN="chargen_openroms.rom" ;;
    pxlfont)  CHARGEN="chargen_pxlfont_2.3.rom" ;;
    *) echo "unknown font '$FONT' (use openroms or pxlfont)" >&2; exit 1 ;;
esac

BASE="https://raw.githubusercontent.com/MEGA65/open-roms/$REF"
mkdir -p "$DEST"

fetch() {
    local source="$1" target="$2" size="$3"
    local url="$BASE/$source"
    echo "Downloading $url"
    curl -fsSL "$url" -o "$DEST/$target"
    local len
    len=$(wc -c < "$DEST/$target" | tr -d ' ')
    if [ "$size" != "0" ] && [ "$len" != "$size" ]; then
        rm -f "$DEST/$target"
        echo "$target is $len bytes, expected $size (did the file layout of open-roms change?)" >&2
        exit 1
    fi
    echo "  -> $DEST/$target ($len bytes)"
}

fetch "bin/basic_generic.rom"  "basic.bin"      8192
fetch "bin/kernal_generic.rom" "kernal.bin"     8192
fetch "bin/$CHARGEN"           "chargen.bin"    4096
fetch "LICENSE"                "LICENSE"        0
fetch "COPYING.LESSER"         "COPYING.LESSER" 0

echo
echo "OpenROMs ($REF) installed in $DEST"
echo "Remember: the .bin files are git-ignored; run this script again after a fresh clone or let CI do it."
