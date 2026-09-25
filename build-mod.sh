#!/usr/bin/env bash
# Build the VsFullCapture mod. Requires an installed game (VINTAGE_STORY).
#   VsFullCapture — Harmony capture of the maximum of server data (for building .vcdbs)
set -euo pipefail

cd "$(dirname "$0")"

if [[ -z "${VINTAGE_STORY:-}" ]]; then
  echo "VINTAGE_STORY is not set — the path to the Vintage Story installation directory." >&2
  echo "  export VINTAGE_STORY=/path/to/VintageStory" >&2
  exit 1
fi

if [[ ! -f "$VINTAGE_STORY/VintagestoryAPI.dll" ]]; then
  echo "There is no VintagestoryAPI.dll in \"$VINTAGE_STORY\" — check the path." >&2
  exit 1
fi

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$PWD/.dotnethome}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$PWD/.nuget/http}"
export DOTNET_NOLOGO=1
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_HTTP_CACHE_PATH"

CONFIG="${CONFIGURATION:-Release}"
MODS_DIR="${VINTAGESTORY_DATA:-$HOME/.config/VintagestoryData}/Mods"

build() {
  local proj="$1" name="$2"
  echo "== $name =="
  dotnet build "$proj" -c "$CONFIG" -v quiet --nologo
  local out
  out="$(dirname "$proj")/bin/$CONFIG/Mods/$name"
  echo "   -> $out"
  if [[ "${INSTALL:-0}" == "1" ]]; then
    mkdir -p "$MODS_DIR"
    rm -rf "${MODS_DIR:?}/$name"
    cp -r "$out" "$MODS_DIR/"
    echo "   installed to $MODS_DIR/$name"
  fi
}

build src/VsFullCapture/VsFullCapture.csproj vsfullcapture

echo
if [[ "${INSTALL:-0}" == "1" ]]; then
  echo "Done. The mod is installed to $MODS_DIR"
else
  echo "Done. To install it into the game right away: INSTALL=1 ./build-mod.sh"
fi
