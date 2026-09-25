#!/usr/bin/env bash
# Builds the VsFullCapture mod and installs it into the Vintage Story Mods directory.
#
#   ./install-mod.sh              build and install VsFullCapture
#   ./install-mod.sh uninstall    remove the mod from the game
#
# The directories can be set with variables:
#   VINTAGE_STORY=/path/to/game          the game installation directory
#   VINTAGESTORY_DATA=/path/to/data      the VintagestoryData directory
set -euo pipefail

cd "$(dirname "$0")"

CONFIG="${CONFIGURATION:-Release}"

# ---------------------------------------------------------------- path detection

find_game_dir() {
  if [[ -n "${VINTAGE_STORY:-}" ]]; then
    [[ -f "$VINTAGE_STORY/VintagestoryAPI.dll" ]] && { echo "$VINTAGE_STORY"; return 0; }
    echo "There is no VintagestoryAPI.dll in VINTAGE_STORY=\"$VINTAGE_STORY\"" >&2
    return 1
  fi
  local candidates=(
    "$HOME/Downloads/vintagestory"
    "$HOME/.local/share/Vintagestory"
    "/usr/share/vintagestory"
    "/opt/vintagestory"
    "$HOME/Library/Application Support/Vintagestory"
    "/Applications/Vintagestory.app/Contents/Resources"
  )
  local d
  for d in "${candidates[@]}"; do
    [[ -f "$d/VintagestoryAPI.dll" ]] && { echo "$d"; return 0; }
  done
  echo "Could not find a Vintage Story installation. Set VINTAGE_STORY=/path/to/game" >&2
  return 1
}

find_data_dir() {
  if [[ -n "${VINTAGESTORY_DATA:-}" ]]; then echo "$VINTAGESTORY_DATA"; return 0; fi
  local candidates=(
    "$HOME/.config/VintagestoryData"
    "$HOME/.var/app/at.vintagestory.VintageStory/config/VintagestoryData"
    "$HOME/Library/Application Support/VintagestoryData"
    "${APPDATA:-}/VintagestoryData"
  )
  local d
  for d in "${candidates[@]}"; do
    [[ -n "$d" && -d "$d" ]] && { echo "$d"; return 0; }
  done
  echo "$HOME/.config/VintagestoryData"
}

GAME_DIR="$(find_game_dir)"
DATA_DIR="$(find_data_dir)"
MODS_DIR="$DATA_DIR/Mods"

# The build references the game DLLs through the VINTAGE_STORY variable — pass the discovered path through.
export VINTAGE_STORY="$GAME_DIR"

# ---------------------------------------------------------------------- install

install_one() {
  local name="$1" proj="$2" out="$3"
  rm -rf "${MODS_DIR:?}/$name"
  cp -r "$out" "$MODS_DIR/$name"
  echo "  + $name"
  find "$MODS_DIR/$name" -maxdepth 1 -type f -printf '      %f (%s bytes)\n' | sort
}

uninstall_one() {
  local name="$1"
  if [[ -d "$MODS_DIR/$name" ]]; then
    rm -rf "${MODS_DIR:?}/$name"
    echo "  - $name removed"
  else
    echo "  $name is not installed"
  fi
}

build_one() {
  local proj="$1"
  dotnet build "$proj" -c "$CONFIG" -v quiet --nologo
}

MODE="${1:-capture}"

echo "Game:   $GAME_DIR"
echo "Data:   $DATA_DIR"
echo "Mods:   $MODS_DIR"
echo

if pgrep -f "Vintagestory.dll|VintagestoryServer.dll" >/dev/null 2>&1; then
  echo "WARNING: the game appears to be running. Mods will only be picked up after a restart."
  echo
fi

mkdir -p "$MODS_DIR"

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$PWD/.dotnethome}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$PWD/.nuget/http}"
export DOTNET_NOLOGO=1
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_HTTP_CACHE_PATH"

case "$MODE" in
  uninstall)
    echo "Uninstall:"
    uninstall_one vsfullcapture
    ;;

  capture)
    echo "Building and installing vsfullcapture:"
    build_one src/VsFullCapture/VsFullCapture.csproj
    install_one vsfullcapture src/VsFullCapture/VsFullCapture.csproj \
                "src/VsFullCapture/bin/$CONFIG/Mods/vsfullcapture"
    ;;

  *)
    echo "Unknown mode: $MODE" >&2
    echo "Usage: $0 [capture|uninstall]" >&2
    exit 1
    ;;
esac

echo
echo "Done."
echo
echo "What to do next:"
echo "  1. Start the game and join a server (or your own world)."
echo "  2. In chat:  .fullcapture where     — where the capture is written"
echo "              .fullcapture status    — how many records"
echo "              .fullcapture flush     — flush to disk"
echo "  3. Play so that the interesting places load completely."
echo "  4. Build the world:"
echo "       ./probe-vcdbs.sh                       # once: build the tools"
echo "       tools/VsVcdbsWriter/bin/$CONFIG/net10.0/vsfullcapture-writer info \"$DATA_DIR/FullCapture/<world>\""
echo
echo "Client log: $DATA_DIR/Logs/client-main.log (look for lines with [vsfullcapture])"
