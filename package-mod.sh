#!/usr/bin/env bash
# Builds the VsFullCapture mod and puts the ready-to-publish archive into dist/.
#
#   ./package-mod.sh                build and package VsFullCapture
#   ./package-mod.sh check <zip>    only verify a ready archive (without building)
#
# Variables:
#   VINTAGE_STORY=/path/to/game     the Vintage Story installation directory
#   CONFIGURATION=Release           build configuration
#   DIST_DIR=dist                   where to put the archives
#   NAME_STYLE=simple|moddb         archive name (see below)
#   KEEP_PDB=1                      keep the debug symbols (.pdb)
#   CLEAN=1                         clean DIST_DIR before building
#   SKIP_PROBE=1                    do not verify the archive with the game's mod loader
#
# Archive names:
#   simple  <modid>_<version>.zip                          — as in the official template
#   moddb   <modid>-v<version>_v<gameversion>.zip          — ModDB recommendation
#
# What is inside: modinfo.json and modicon.png in the root, the dll next to them,
# assets/ if present. The structure rules are dictated by the game's mod loader
# (Vintagestory.Common.ModContainer) — see tools/package_mod.py.
set -euo pipefail

cd "$(dirname "$0")"

CONFIG="${CONFIGURATION:-Release}"
DIST_DIR="${DIST_DIR:-$PWD/dist}"
NAME_STYLE="${NAME_STYLE:-simple}"
PYTHON="${PYTHON:-python3}"

# ---------------------------------------------------------------- environment checks

if ! command -v "$PYTHON" >/dev/null 2>&1; then
  echo "python3 is required (archive packaging). Install it or set PYTHON=/path/to/python3" >&2
  exit 1
fi

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

GAME_DIR=""

# Path to the built probe: the TFM is not hardcoded (it changes together with TargetFramework).
PROBE_DLL=""
probe_dll() {
  if [[ -z "$PROBE_DLL" ]]; then
    shopt -s nullglob
    local candidates=(tools/VsVcdbsProbe/bin/"$CONFIG"/*/VsVcdbsProbe.dll)
    shopt -u nullglob
    [[ ${#candidates[@]} -gt 0 ]] || { echo "Built VsVcdbsProbe.dll not found" >&2; return 1; }
    PROBE_DLL="${candidates[0]}"
  fi
  echo "$PROBE_DLL"
}

# sha256sum is not available everywhere (on macOS it is shasum); a missing hash must not break the script.
print_hash() {
  local file="$1"
  if command -v sha256sum >/dev/null 2>&1; then
    ( cd "$(dirname "$file")" && sha256sum "$(basename "$file")" )
  elif command -v shasum >/dev/null 2>&1; then
    ( cd "$(dirname "$file")" && shasum -a 256 "$(basename "$file")" )
  else
    echo "(sha256 unavailable) $file"
  fi
}

# The game's native libraries: Linux — LD_LIBRARY_PATH, macOS — DYLD_LIBRARY_PATH.
setup_game_libs() {
  export LD_LIBRARY_PATH="$GAME_DIR/Lib:${LD_LIBRARY_PATH:-}"
  if [[ "$(uname -s)" == "Darwin" ]]; then
    export DYLD_LIBRARY_PATH="$GAME_DIR/Lib:${DYLD_LIBRARY_PATH:-}"
  fi
}

pack_one() {
  local name="$1" mod_dir="$2" bin_dir="$3"

  local modid version game
  modid="$("$PYTHON" tools/package_mod.py read "$mod_dir/modinfo.json" modid)"
  version="$("$PYTHON" tools/package_mod.py read "$mod_dir/modinfo.json" version)"
  game="$("$PYTHON" tools/package_mod.py read "$mod_dir/modinfo.json" game || true)"

  local file
  case "$NAME_STYLE" in
    simple) file="${modid}_${version}.zip" ;;
    moddb)  file="${modid}-v${version}_v${game:-unknown}.zip" ;;
    *)      echo "Unknown NAME_STYLE=\"$NAME_STYLE\" (simple or moddb expected)" >&2; return 1 ;;
  esac
  local out="$DIST_DIR/$file"

  echo "== $name =="
  local proj="$mod_dir/$(basename "$mod_dir").csproj"
  [[ -f "$proj" ]] || { echo "No project $proj" >&2; return 1; }
  dotnet build "$proj" -c "$CONFIG" -v quiet --nologo

  local extra=()
  [[ "${KEEP_PDB:-0}" == "1" ]] && extra+=(--keep-pdb)

  "$PYTHON" tools/package_mod.py pack \
      --mod-dir "$mod_dir" --bin-dir "$bin_dir" --output "$out" ${extra[@]+"${extra[@]}"}

  if [[ "${SKIP_PROBE:-0}" != "1" ]]; then
    echo
    echo "   verification with the game's mod loader:"
    setup_game_libs
    dotnet "$(probe_dll)" modzip \
        "$out" --modid "$modid" --version "$version" | sed 's/^/   /'
  fi

  echo
  print_hash "$out"
  echo
}

check_one() {
  local zip="$1" status=0
  [[ -f "$zip" ]] || { echo "No file \"$zip\"" >&2; return 1; }
  echo "== $zip =="
  local extra=()
  [[ "${KEEP_PDB:-0}" == "1" ]] && extra+=(--keep-pdb)
  # Our own structure check, then the game's mod loader verdict. Both are always
  # run so that both reasons are visible in the report.
  "$PYTHON" tools/package_mod.py verify "$zip" ${extra[@]+"${extra[@]}"} || status=1
  if [[ "${SKIP_PROBE:-0}" != "1" ]]; then
    echo "   verification with the game's mod loader:"
    setup_game_libs
    # Read the id and version from the archive itself so the probe checks them, not just the structure.
    local modid version
    modid="$("$PYTHON" -c "import json,sys,zipfile;print(json.loads(zipfile.ZipFile(sys.argv[1]).read('modinfo.json'))['modId'])" "$zip" 2>/dev/null || true)"
    version="$("$PYTHON" -c "import json,sys,zipfile;print(json.loads(zipfile.ZipFile(sys.argv[1]).read('modinfo.json'))['version'])" "$zip" 2>/dev/null || true)"
    local args=(modzip "$zip")
    [[ -n "$modid" ]] && args+=(--modid "$modid")
    [[ -n "$version" ]] && args+=(--version "$version")
    dotnet "$(probe_dll)" "${args[@]}" | sed 's/^/   /' || status=1
  fi
  echo
  return $status
}

MODE="${1:-capture}"

# ------------------------------------------------------------------ preparation

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$PWD/.dotnethome}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$PWD/.nuget/http}"
export DOTNET_NOLOGO=1
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_HTTP_CACHE_PATH"

GAME_DIR=""
if [[ "${SKIP_PROBE:-0}" != "1" ]]; then
  GAME_DIR="$(find_game_dir)"
  export VINTAGE_STORY="$GAME_DIR"
  echo "Game:   $GAME_DIR"
fi
if [[ "$MODE" != "check" ]]; then
  echo "Archives: $DIST_DIR"
fi
echo

# Verify the archive not with our own code but with the game's mod loader (ModContainer).
if [[ -n "$GAME_DIR" ]]; then
  echo "== VsVcdbsProbe (archive verification with the game's code) =="
  dotnet build tools/VsVcdbsProbe/VsVcdbsProbe.csproj -c "$CONFIG" -v quiet --nologo
  echo
fi

if [[ "$MODE" != "check" ]]; then
  mkdir -p "$DIST_DIR"
  [[ "${CLEAN:-0}" == "1" ]] && rm -f "$DIST_DIR"/*.zip
fi

case "$MODE" in
  capture)
    pack_one vsfullcapture src/VsFullCapture "src/VsFullCapture/bin/$CONFIG/Mods/vsfullcapture"
    ;;
  check)
    shift
    [[ $# -gt 0 ]] || { echo "Usage: $0 check <archive.zip> [...]" >&2; exit 1; }
    for zip in "$@"; do check_one "$zip"; done
    exit 0
    ;;
  *)
    echo "Unknown mode: $MODE" >&2
    echo "Usage: $0 [capture|check <archive.zip>]" >&2
    exit 1
    ;;
esac

echo "Done."
echo
echo "What next:"
echo "  1. Open https://mods.vintagestory.at/edit/mod, fill in the mod page."
echo "  2. \"Add release\" → upload the archive from $DIST_DIR."
echo "  3. Specify the compatible game versions and a changelog, then save."
echo
echo "Verify the archive before submitting:  ./package-mod.sh check <archive.zip>"
