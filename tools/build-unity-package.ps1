<#
.SYNOPSIS
    Builds the emulator for .NET Standard 2.1 and copies the assemblies into the Unity package.

.DESCRIPTION
    Runs `dotnet build -c <Configuration> -f netstandard2.1` for Tedd.Commodore.Emulator, Tedd.Commodore.Hosting,
    Tedd.Commodore.Embedding and Tedd.Commodore.Unity, then copies the four DLLs (and their .pdb files) into
    unity/com.tedd.commodore/Runtime/Plugins/, where Runtime/Tedd.Commodore.asmdef references them.

.PARAMETER Configuration
    Build configuration, default Release.

.EXAMPLE
    pwsh tools/build-unity-package.ps1
    pwsh tools/build-unity-package.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$projects = @("Tedd.Commodore.Emulator", "Tedd.Commodore.Hosting", "Tedd.Commodore.Embedding", "Tedd.Commodore.Unity")
$plugins = Join-Path $root "unity/com.tedd.commodore/Runtime/Plugins"

foreach ($project in $projects) {
    $csproj = Join-Path $root "src/$project/$project.csproj"
    Write-Host "Building $project ($Configuration, netstandard2.1)"
    & dotnet build $csproj -c $Configuration -f netstandard2.1 -nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $project (exit code $LASTEXITCODE)"
    }
}

New-Item -ItemType Directory -Force -Path $plugins | Out-Null

foreach ($project in $projects) {
    $outDir = Join-Path $root "src/$project/bin/$Configuration/netstandard2.1"
    $dll = Join-Path $outDir "$project.dll"
    if (-not (Test-Path $dll)) {
        throw "Expected build output not found: $dll"
    }
    Copy-Item -Path $dll -Destination $plugins -Force
    $pdb = Join-Path $outDir "$project.pdb"
    if (Test-Path $pdb) {
        Copy-Item -Path $pdb -Destination $plugins -Force
    }
    Write-Host "Copied $project.dll to $plugins"
}

Write-Host "Done. Add unity/com.tedd.commodore to a Unity project with 'Add package from disk' (see unity/README.md)."
