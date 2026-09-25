#!/usr/bin/env python3
"""Pack a built mod into an archive for publication (ModDB) and verify the archive.

Why this is a separate step. The game loads a mod from a ZIP under strict rules
(Vintagestory.Common.ModContainer, 1.22.7):

  * ``modinfo.json`` is read as a ZipEntry with exactly that name, so the file
    must live IN THE ROOT of the archive, not in a subfolder named after the mod;
  * ``.dll`` files are only allowed in the archive root (the exception is
    ``native/`` for unmanaged libraries); a DLL in a nested folder means
    "Won't load this mod";
  * ``.cs`` files are only allowed under ``src/``; otherwise the mod is marked
    as failed to load;
  * ``modicon.png`` (or the path from ``iconPath``) is looked up in the root.

This script builds the archive according to those rules and immediately checks
its structure and ``modinfo.json``. It does NOT load the mod assembly, so a
crash in a ``ModSystem`` or an incompatibility with the game version will not be
caught by this check.

Usage:
    package_mod.py read <modinfo.json> <modid|version|game|name|side>
    package_mod.py pack --mod-dir <src/Mod> --bin-dir <bin/Release/Mods/mod>
                        --output <file.zip> [--keep-pdb]
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import zipfile
from pathlib import Path
from typing import NoReturn

# ModInfo.IsValidModID: the first character is a lowercase letter, the rest are
# lowercase letters and digits.
MODID_RE = re.compile(r"^[a-z][a-z0-9]*$")

# What we take from the build directory. .dll/.json go to the root, native files
# (.so/.dylib) go to native/: that is where the game's mod loader expects
# unmanaged libraries.
BIN_KEEP = {".dll", ".json", ".so", ".dylib"}
NATIVE_SUFFIXES = {".so", ".dylib"}


def die(message: str) -> NoReturn:
    print("error: " + message, file=sys.stderr)
    sys.exit(1)


def load_modinfo(modinfo: Path) -> dict:
    if not modinfo.is_file():
        die(f"missing file {modinfo}")
    try:
        return json.loads(modinfo.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        die(f"{modinfo} is not valid JSON: {e}")


def read_field(modinfo: Path, key: str) -> str:
    data = load_modinfo(modinfo)
    if key == "game":
        deps = data.get("dependencies") or {}
        return str(deps.get("game", "") or "")
    value = data.get(key)
    if value is None and key == "modid":
        value = data.get("modId")
    return "" if value is None else str(value)


def collect(mod_dir: Path, bin_dir: Path, keep_pdb: bool) -> list[tuple[Path, str]]:
    """Return the list of (file on disk, path inside the archive) in order."""
    entries: list[tuple[Path, str]] = []

    # 1. modinfo.json and the icon always come from the SOURCES, so that the copy
    #    in bin/ cannot end up being stale.
    entries.append((mod_dir / "modinfo.json", "modinfo.json"))
    icon = mod_dir / "modicon.png"
    if icon.is_file():
        entries.append((icon, "modicon.png"))

    # 2. Assemblies and other files from the build directory (root only:
    #    ModContainer will not accept a DLL from a nested folder).
    if not bin_dir.is_dir():
        die(f"missing build directory {bin_dir} — build the mod first")
    binaries = 0
    for path in sorted(bin_dir.iterdir()):
        if not path.is_file():
            continue
        suffix = path.suffix.lower()
        if path.name == "modinfo.json" or path.name == "modicon.png":
            continue  # already added from the sources
        if suffix == ".pdb" and not keep_pdb:
            continue
        if suffix not in BIN_KEEP:
            continue
        arc = ("native/" + path.name) if suffix in NATIVE_SUFFIXES else path.name
        entries.append((path, arc))
        if suffix == ".dll":
            binaries += 1
    if binaries == 0:
        die(f"no .dll found in {bin_dir} — did the mod fail to build?")

    # 3. assets/ is the mod's content; take it from the sources as a whole.
    assets = mod_dir / "assets"
    if assets.is_dir():
        for path in sorted(assets.rglob("*")):
            if path.is_file():
                entries.append((path, "assets/" + path.relative_to(assets).as_posix()))

    return entries


def safe_arcname(name: str) -> str:
    name = name.replace("\\", "/")
    if name.startswith("/") or ".." in name.split("/") or ":" in name:
        die(f"invalid path inside the archive: {name}")
    return name


def pack(args: argparse.Namespace) -> int:
    mod_dir = Path(args.mod_dir)
    bin_dir = Path(args.bin_dir)
    output = Path(args.output)

    modinfo = mod_dir / "modinfo.json"
    modid = read_field(modinfo, "modid")
    version = read_field(modinfo, "version")

    if not modid or not MODID_RE.match(modid):
        die(f"modid {modid!r} is not valid: the first character must be a lowercase letter, "
            "the rest lowercase letters and digits")
    if not version:
        die("modinfo.json has no version — ModDB will not accept the release")

    authors = [str(a) for a in (load_modinfo(modinfo).get("authors") or [])]
    if not authors or authors == ["you"]:
        print("WARNING: modinfo.json has no authors filled in — the ModDB page will "
              "show no name. Edit " + str(modinfo), file=sys.stderr)

    entries = collect(mod_dir, bin_dir, args.keep_pdb)
    seen: set[str] = set()
    for _, arc in entries:
        safe_arcname(arc)
        if arc in seen:
            die(f"path {arc} was added to the archive twice")
        seen.add(arc)

    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for path, arc in entries:
            if not path.is_file():
                die(f"missing file {path}")
            zf.write(path, arc)

    print(f"modid={modid} version={version} name={read_field(modinfo, 'name')!r}")
    return verify_archive(output, keep_pdb=args.keep_pdb, modid=modid, version=version)


def verify_archive(output: Path, keep_pdb: bool = False,
                   modid: str | None = None, version: str | None = None) -> int:
    """Check a finished archive against the game mod loader's rules."""
    if not output.is_file():
        die(f"missing file {output}")

    problems: list[str] = []
    with zipfile.ZipFile(output) as zf:
        names = zf.namelist()

        if "modinfo.json" not in names:
            problems.append("modinfo.json is not in the archive root")
        else:
            try:
                packed_info = json.loads(zf.read("modinfo.json").decode("utf-8"))
            except (json.JSONDecodeError, UnicodeDecodeError) as e:
                problems.append(f"modinfo.json inside the archive cannot be read: {e}")
                packed_info = {}
            packed_modid = packed_info.get("modid", packed_info.get("modId"))
            if not packed_modid:
                problems.append("the archive's modinfo.json has no modid")
            if modid and packed_modid != modid:
                problems.append(f"modid in the archive is {packed_modid!r}, expected {modid!r}")
            if version and str(packed_info.get("version", "")) != version:
                problems.append(f"version in the archive is {packed_info.get('version')!r}, "
                                f"expected {version!r}")

        for name in names:
            if name.endswith("/"):
                continue
            top = name.split("/")[0] if "/" in name else None
            suffix = Path(name).suffix.lower()
            if suffix == ".dll" and top not in (None, "native"):
                problems.append(f"DLL is not in the archive root: {name}")
            if suffix in NATIVE_SUFFIXES and top != "native":
                problems.append(f"native library is not under native/: {name}")
            if suffix == ".cs" and top != "src":
                problems.append(f".cs outside src/: {name}")
            if suffix == ".pdb" and not keep_pdb:
                problems.append(f"debug symbols in the archive: {name}")

    print(f"archive: {output} ({output.stat().st_size} bytes, {len(names)} files)")
    for name in names:
        print(f"  {name}")

    if problems:
        for p in problems:
            print("FAIL: " + p, file=sys.stderr)
        return 1

    print("OK: the archive structure and modinfo.json follow the mod loader's rules "
          "(.pdb and native/ are the packager's own rules; the game does not load "
          "the mod assembly here)")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Pack a Vintage Story mod into an archive for publication")
    sub = parser.add_subparsers(dest="command", required=True)

    p_read = sub.add_parser("read", help="read a field from modinfo.json")
    p_read.add_argument("modinfo")
    p_read.add_argument("key", choices=["modid", "version", "game", "name", "side"])

    p_pack = sub.add_parser("pack", help="build the archive and verify it")
    p_pack.add_argument("--mod-dir", required=True,
                        help="mod source directory (modinfo.json, assets)")
    p_pack.add_argument("--bin-dir", required=True,
                        help="mod build directory (bin/Release/Mods/<modid>)")
    p_pack.add_argument("--output", required=True, help="where to write the .zip")
    p_pack.add_argument("--keep-pdb", action="store_true", help="keep debug symbols")

    p_verify = sub.add_parser("verify", help="verify a finished archive")
    p_verify.add_argument("archive")
    p_verify.add_argument("--keep-pdb", action="store_true", help="do not treat .pdb as an error")
    p_verify.add_argument("--modid", default=None)
    p_verify.add_argument("--version", default=None)

    args = parser.parse_args(argv)
    if args.command == "read":
        value = read_field(Path(args.modinfo), args.key)
        print(value)
        return 0 if value else 1
    if args.command == "verify":
        return verify_archive(Path(args.archive), keep_pdb=args.keep_pdb,
                              modid=args.modid, version=args.version)
    return pack(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
