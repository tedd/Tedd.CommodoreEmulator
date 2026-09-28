# ROM images for the browser build

The web front-end looks for these files at startup (relative to the site root):

| File          | Size  | Content                                  |
|---------------|-------|------------------------------------------|
| `basic.bin`   | 8192  | BASIC ROM ($A000-$BFFF)                  |
| `kernal.bin`  | 8192  | KERNAL ROM ($E000-$FFFF)                 |
| `chargen.bin` | 4096  | character generator ROM                  |
| `1541.bin`    | 16384 | 1541 DOS ROM ($C000-$FFFF), optional     |

`*.bin` and `*.rom` in this directory are **git-ignored**: the original Commodore ROMs are copyrighted and
must not be committed. Two ways to fill the directory:

1. **OpenROMs** (free, LGPL-3.0): run `tools/fetch-openroms.ps1` (PowerShell) or `tools/fetch-openroms.sh`.
   They download `bin/basic_generic.rom`, `bin/kernal_generic.rom` and `bin/chargen_openroms.rom` from
   <https://github.com/MEGA65/open-roms> as `basic.bin`/`kernal.bin`/`chargen.bin`, together with the
   OpenROMs `LICENSE` and `COPYING.LESSER`. OpenROMs has no 1541 DOS, so no drive is attached.
   The GitHub Pages deploy job runs the same script, which is why the public demo boots OpenROMs.
2. **Your own ROM dumps**: copy them here with the names above (a combined 16K `64c.251913-01.bin` is
   *not* recognised here; split it, or use the "Load your own ROMs" panel in the running app, which also
   accepts the combined file and the 8K+8K 1541 split and keeps the images in the browser's localStorage).

Nothing is served from here at runtime except these files; the app never uploads ROMs anywhere.
