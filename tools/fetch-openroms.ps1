<#
.SYNOPSIS
    Downloads the free OpenROMs C64 ROM images into src/Tedd.Commodore.Web/wwwroot/roms.

.DESCRIPTION
    OpenROMs (https://github.com/MEGA65/open-roms, LGPL-3.0) is a clean-room re-implementation of the
    Commodore 64 BASIC, KERNAL and character generator ROMs. The project publishes no GitHub releases;
    the pre-built images live in the "bin/" directory of the repository and are served through
    raw.githubusercontent.com. This script fetches:

        bin/basic_generic.rom    (8192 bytes)  -> roms/basic.bin
        bin/kernal_generic.rom   (8192 bytes)  -> roms/kernal.bin
        bin/chargen_openroms.rom (4096 bytes)  -> roms/chargen.bin
        LICENSE                                 -> roms/LICENSE          (OpenROMs copyright notice, LGPL-3.0)
        COPYING.LESSER                          -> roms/COPYING.LESSER   (full LGPL-3.0 text)

    OpenROMs provides no 1541 DOS ROM, so the browser build boots without a disk drive unless the user
    uploads a real 1541 ROM through the "Load your own ROMs" panel.

    The .bin files are git-ignored (see .gitignore); only README.md, LICENSE and COPYING.LESSER are committed.

.PARAMETER Ref
    Git ref (branch, tag or commit) of the open-roms repository to download from. Default: master.

.PARAMETER Destination
    Target directory. Default: <repo>/src/Tedd.Commodore.Web/wwwroot/roms.

.PARAMETER Font
    Character set to use: "openroms" (default, chargen_openroms.rom) or "pxlfont" (chargen_pxlfont_2.3.rom).

.EXAMPLE
    pwsh tools/fetch-openroms.ps1
    pwsh tools/fetch-openroms.ps1 -Ref 0e1f2a3 -Font pxlfont
#>
[CmdletBinding()]
param(
    [string]$Ref = "master",
    [string]$Destination = "",
    [ValidateSet("openroms", "pxlfont")]
    [string]$Font = "openroms"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path (Split-Path -Parent $PSScriptRoot) "src/Tedd.Commodore.Web/wwwroot/roms"
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$base = "https://raw.githubusercontent.com/MEGA65/open-roms/$Ref"
$chargen = if ($Font -eq "pxlfont") { "chargen_pxlfont_2.3.rom" } else { "chargen_openroms.rom" }

# source path in the repository, local file name, expected size in bytes (0 = any)
$files = @(
    @{ Source = "bin/basic_generic.rom";  Target = "basic.bin";      Size = 8192 },
    @{ Source = "bin/kernal_generic.rom"; Target = "kernal.bin";     Size = 8192 },
    @{ Source = "bin/$chargen";           Target = "chargen.bin";    Size = 4096 },
    @{ Source = "LICENSE";                Target = "LICENSE";        Size = 0 },
    @{ Source = "COPYING.LESSER";         Target = "COPYING.LESSER"; Size = 0 }
)

foreach ($f in $files) {
    $url = "$base/$($f.Source)"
    $target = Join-Path $Destination $f.Target
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
    $len = (Get-Item $target).Length
    if ($f.Size -gt 0 -and $len -ne $f.Size) {
        Remove-Item $target -Force
        throw "$($f.Target) is $len bytes, expected $($f.Size) (did the file layout of open-roms change?)"
    }
    Write-Host "  -> $target ($len bytes)"
}

Write-Host ""
Write-Host "OpenROMs ($Ref) installed in $Destination"
Write-Host "Remember: the .bin files are git-ignored; run this script again after a fresh clone or let CI do it."
