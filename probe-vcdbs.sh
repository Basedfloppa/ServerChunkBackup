#!/usr/bin/env bash
# Verification of the game tools: they EXECUTE the game's code offline, the game itself is not started.
#   tools/VsVcdbsProbe  — proves that .vcdbs is built from the package data
#   tools/VsVcdbsWriter — the .vcdbs writer + end-to-end self-test "capture -> world"
set -euo pipefail

cd "$(dirname "$0")"

# The game is mandatory: the tools execute its code and take assemblies from it.
# The directory is searched in the typical places, but it is better to set it explicitly: VINTAGE_STORY=/path/to/game
if [[ -z "${VINTAGE_STORY:-}" ]]; then
  for d in \
    "$HOME/Downloads/vintagestory" \
    "$HOME/.local/share/Vintagestory" \
    "/usr/share/vintagestory" \
    "/opt/vintagestory" \
    "$HOME/Library/Application Support/Vintagestory" \
    "/Applications/Vintagestory.app/Contents/Resources"
  do
    if [[ -f "$d/VintagestoryLib.dll" ]]; then VINTAGE_STORY="$d"; break; fi
  done
fi
if [[ -z "${VINTAGE_STORY:-}" ]]; then
  echo "The Vintage Story installation directory was not found." >&2
  echo "  export VINTAGE_STORY=/path/to/VintageStory" >&2
  exit 1
fi
export VINTAGE_STORY
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$PWD/.dotnethome}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$PWD/.nuget/http}"
export DOTNET_NOLOGO=1
# The native libe_sqlite3.so lives in the game's Lib.
export LD_LIBRARY_PATH="$VINTAGE_STORY/Lib:${LD_LIBRARY_PATH:-}"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_HTTP_CACHE_PATH"

if [[ ! -f "$VINTAGE_STORY/VintagestoryLib.dll" ]]; then
  echo "VintagestoryLib.dll not found in \"$VINTAGE_STORY\" — set VINTAGE_STORY." >&2
  exit 1
fi

CONFIG="${CONFIGURATION:-Release}"

echo "== VsVcdbsProbe =="
dotnet build tools/VsVcdbsProbe/VsVcdbsProbe.csproj -c "$CONFIG" -v quiet --nologo
dotnet "tools/VsVcdbsProbe/bin/$CONFIG/net10.0/VsVcdbsProbe.dll" "$@"

echo
echo "== VsVcdbsWriter: self-test =="
dotnet build tools/VsVcdbsWriter/VsVcdbsWriter.csproj -c "$CONFIG" -v quiet --nologo
dotnet "tools/VsVcdbsWriter/bin/$CONFIG/net10.0/vsfullcapture-writer.dll" selftest

# Packaged mod archives (./package-mod.sh) — verify them with the game's mod loader.
shopt -s nullglob
archives=("$PWD"/dist/*.zip)
if [[ ${#archives[@]} -gt 0 ]]; then
  echo
  echo "== mod archives in dist/ =="
  dotnet "tools/VsVcdbsProbe/bin/$CONFIG/net10.0/VsVcdbsProbe.dll" modzip "${archives[@]}"
fi
