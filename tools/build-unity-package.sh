#!/usr/bin/env bash
# Builds the emulator for .NET Standard 2.1 and copies the assemblies into the Unity package.
#
# Runs `dotnet build -c <configuration> -f netstandard2.1` for Tedd.Commodore.Emulator, Tedd.Commodore.Hosting,
# Tedd.Commodore.Embedding and Tedd.Commodore.Unity, then copies the four DLLs (and their .pdb files) into
# unity/com.tedd.commodore/Runtime/Plugins/, where Runtime/Tedd.Commodore.asmdef references them.
#
# Usage: tools/build-unity-package.sh [Release|Debug]
set -euo pipefail

CONFIGURATION="${1:-Release}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECTS=(Tedd.Commodore.Emulator Tedd.Commodore.Hosting Tedd.Commodore.Embedding Tedd.Commodore.Unity)
PLUGINS="$ROOT/unity/com.tedd.commodore/Runtime/Plugins"

for project in "${PROJECTS[@]}"; do
    echo "Building $project ($CONFIGURATION, netstandard2.1)"
    dotnet build "$ROOT/src/$project/$project.csproj" -c "$CONFIGURATION" -f netstandard2.1 -nologo
done

mkdir -p "$PLUGINS"

for project in "${PROJECTS[@]}"; do
    out="$ROOT/src/$project/bin/$CONFIGURATION/netstandard2.1"
    if [ ! -f "$out/$project.dll" ]; then
        echo "Expected build output not found: $out/$project.dll" >&2
        exit 1
    fi
    cp -f "$out/$project.dll" "$PLUGINS/"
    if [ -f "$out/$project.pdb" ]; then
        cp -f "$out/$project.pdb" "$PLUGINS/"
    fi
    echo "Copied $project.dll to $PLUGINS"
done

echo "Done. Add unity/com.tedd.commodore to a Unity project with 'Add package from disk' (see unity/README.md)."
