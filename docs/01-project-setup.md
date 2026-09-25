# 1. Project setup and build

## 1.1. What you need

| What | Version | Verified here |
|---|---|---|
| .NET SDK | **10.0** | `dotnet 10.0.112` |
| Installed game | 1.22.x | any 1.22.7 installation (path set via `VINTAGE_STORY`) |
| Editor | any | VS Code / Rider / Visual Studio |

SDK installation: <https://dotnet.microsoft.com/download>. Make sure the major
version matches the target (`net10.0` for 1.22.x).

> **Important note about NuGet.** There is **no** `VintagestoryAPI` package on NuGet
> (verified by searching the gallery: anegostudios publishes only
> `VintageStory.Mod.Templates` and `VintageStory.Mod.BasicTemplate` — those are
> `dotnet new` templates, not the API). The API is referenced **via a DLL from the installed game**.
> Outdated instructions with `<PackageReference Include="VintagestoryAPI">` can be found online
> — they do not work.

## 1.2. The `VINTAGE_STORY` variable

The project references `$(VINTAGE_STORY)/VintagestoryAPI.dll`. Set an environment
variable to the game installation directory (where `VintagestoryAPI.dll` lives):

```bash
# Linux / macOS
export VINTAGE_STORY="$HOME/.local/share/Vintagestory"     # or the path from the installer
# Windows (cmd)
set VINTAGE_STORY=C:\Program Files\Vintagestory
# Windows (PowerShell)
$env:VINTAGE_STORY = "C:\Program Files\Vintagestory"
```

Or skip the variable and put the path directly in the `.csproj`:

```xml
<PropertyGroup>
  <VINTAGE_STORY>/path/to/VintageStory</VINTAGE_STORY>
</PropertyGroup>
```

## 1.3. Starting a project

This repository was created by hand — no template — and
`src/VsFullCapture/VsFullCapture.csproj` is the reference. For a new mod the official
template is the easier route:

```bash
dotnet new install VintageStory.Mod.Templates
dotnet new vsmod --AddSampleCode -o MyMod      # modinfo.json + assets + csproj
```

It also has switches for the references a code mod needs: `IncludeHarmony`,
`IncludeSQLite`, `IncludeVintagestoryLib`, `IncludeVSCode`.

One gotcha either way: reference the game assemblies with `<Private>false</Private>`,
otherwise the game DLL is copied into your mod folder and causes a load conflict.

## 1.4. A mod with multiple projects

Vintage Story loads the assemblies it finds in the mod folder, but it is safer to keep
**a single DLL**. If the logic lives in a separate library (`VsChunkDump.Core` in
this repository), include it as **source files**, not as an assembly reference:

```xml
<ItemGroup>
  <Compile Include="..\VsChunkDump.Core\*.cs" LinkBase="Core" />
</ItemGroup>
```

That way the shared library builds, tests and is reused like an ordinary
project (`dotnet test`), while the mod stays a single self-contained `DLL`.

## 1.5. Building

The core and tests (`VsChunkDump.Core`, `VsChunkDump.Cli`, tests) build with two
commands and do not need an installed game:

```bash
dotnet build VsChunkDump.slnx -c Release
dotnet test  VsChunkDump.slnx -c Release --no-build
```

The mod builds separately and requires an installed game (`VINTAGE_STORY`):

```bash
export VINTAGE_STORY=/path/to/VintageStory
./build-mod.sh          # = dotnet build src/VsFullCapture/VsFullCapture.csproj
```

Result: `src/VsFullCapture/bin/Release/Mods/vsfullcapture/` containing
`modinfo.json` and `VsFullCapture.dll`.

To install, either run `INSTALL=1 ./build-mod.sh` or copy the folder into
`<VintagestoryData>/Mods/`:

```bash
cp -r src/VsFullCapture/bin/Release/Mods/vsfullcapture "$HOME/.config/VintagestoryData/Mods/"
```

or (if the game runs from a local installation) point `OutputPath` straight at the
game directory's `Mods/`.

## 1.6. Debugging

The template generates `.vscode/launch.json` and `tasks.json`. The main scenario is
launching the **client** under the debugger:

```jsonc
{
  "type": "coreclr",
  "request": "launch",
  "name": "Launch Game (Debug)",
  "program": "${env:VINTAGE_STORY}/Vintagestory.dll",
  "args": [ "--dataPath", "${env:VINTAGE_STORY}/../VintagestoryData" ],
  "cwd": "${env:VINTAGE_STORY}",
  "console": "internalConsole"
}
```

Client log: `<VintagestoryData>/Logs/client-main.log`. Look for lines containing
`[vsfullcapture]` (the prefix is set in `Mod.Logger`).

Quick check that the mod loaded: in the game, open the mod list
(`Settings → Mods`) — the name, version and icon appear there. If the mod is missing,
check the log: it is almost always a `modinfo.json` schema error or a `side`
mismatch.

## 1.7. Common problems

| Symptom | Cause / fix |
|---|---|
| `error CS0006: metadata file 'VintagestoryAPI.dll' could not be found` | `VINTAGE_STORY` is not set, or the path does not point to the directory containing the DLL |
| `Wrong VintagestoryAPI.dll version` at startup | the mod was built against a different game version; rebuild after updating the path/game |
| Mod is not visible in the list | `modinfo.json` was not found, is not in the mod folder, or has a schema error (`additionalProperties: false` forbids extra fields) |
| `modid` rejected | pattern `^[a-z][a-z0-9]*$` (`ModInfo.IsValidModID`): the **first** character must be a lowercase Latin letter, the rest lowercase letters and digits — `9mod` is invalid |
| Mod loads but `StartClientSide` is not called | `side` = `Server`, or `ShouldLoad` returned `false` |
| Server-side commands do not work | with `side: Client` the server part does not load even in singleplayer — use `Universal` |
| Client is kicked with "mod mismatch" | server and client have the mod with different `version`/`networkVersion`; sync the versions or set `requiredOnClient/Server: false` |
| `api.World.Blocks` is empty in `StartClientSide` | blocks are registered later on the client — defer to the first tick or `AssetsFinalize` |
| `dotnet build` fails on NuGet because `~/.nuget` is read-only | see `nuget.config` at the repository root: `globalPackagesFolder` is moved inside the project |

## 1.8. Packaging a mod for publication (ModDB)

A finished mod is distributed as a single zip archive, and the archive structure is **not
free-form** — it is dictated by the game's mod loader `Vintagestory.Common.ModContainer`
(decompile 1.22.7, methods `Unpack` and `LoadModInfo`):

| Rule | What happens if you break it |
|---|---|
| `modinfo.json` must sit **at the archive root** (looked up as a `ZipEntry` named exactly `modinfo.json`) | `Missing modinfo.json`, the mod will not load |
| `.dll` files only at the archive root, except `native/` for unmanaged libraries | `File '...' is not in the mod's root folder. Won't load this mod.` |
| `.cs` files only under the `src/` subfolder | the mod is flagged with a load error |
| `modicon.png` (or the path from `iconPath`) at the root | the mod has no icon |
| the archive goes into `<VintagestoryData>/Mods/` | — |

In other words, you **must not** put the mod files in a subfolder named after the mod:
the game rejects an “archive with a folder inside”. That is exactly why packaging is a
separate step in this repository, rather than “zipped the directory — done”.

### Building the archive

```bash
export VINTAGE_STORY=/path/to/VintageStory
./package-mod.sh                    # vsfullcapture -> dist/
NAME_STYLE=moddb ./package-mod.sh   # archive names include the game version
```

The script does three things:

1. builds the mod in `Release`;
2. lays out the archive according to the rules above (`tools/package_mod.py`): `modinfo.json`
   and `modicon.png` come from the sources, the assemblies from `bin/Release/Mods/<modid>`,
   and the `assets/` directory (if it appears) is added in full. It also validates
   `modinfo.json` (valid JSON, `modid` matching `^[a-z][a-z0-9]*$`, non-empty
   `version`) and checks that debug symbols, sources and nested
   directories did not make it into the archive;
3. **verifies the archive with the game's own mod loader**: `tools/VsVcdbsProbe modzip <archive>`
   unpacks the zip with the `ModContainer` class, reads `modinfo.json` with the game's
   `ModInfo` and prints the assemblies it found and their `Status`. This is the same code
   that runs when the game starts, so “it built for me but does not load for others”
   is caught before publication rather than after the complaints.

The archive contains the assembly `dll` and its `deps.json`. `deps.json` is not used by
the game — the loader resolves dependencies through `AssemblyResolve` over the mod
directories (`ModAssemblyLoader`) — but it does no harm either; the official template
ships it the same way.

Archive name:

| `NAME_STYLE` | Example | When |
|---|---|---|
| `simple` (default) | `vsfullcapture_1.0.0.zip` | as in the official template (`CakeBuild/Program.cs`) |
| `moddb` | `vsfullcapture-v1.0.0_v1.22.7.zip` | [forum recommendation](https://www.vintagestory.at/forums/topic/18633-upload-your-mod-to-the-moddb/): the name shows both the mod version and the game version |

Check an already built archive without rebuilding anything:

```bash
./package-mod.sh check dist/vsfullcapture_1.0.0.zip
```

`./probe-vcdbs.sh` runs the same check: it executes game code offline and at the
end inspects all archives in `dist/`, if there are any.

Useful variables: `DIST_DIR` (defaults to `dist/`), `CLEAN=1` (delete
previous archives), `KEEP_PDB=1` (keep `.pdb`), `SKIP_PROBE=1` (skip the
game-loader check — in case there is no installed game at hand).

### Publishing

1. <https://mods.vintagestory.at/edit/mod> → **Submit a mod**: name, short
   description, screenshots, save.
2. On the mod page → **Add release** → upload the zip. ModDB takes the **Mod Id** and
   **Mod Version Number** fields from the `modinfo.json` inside the archive and will
   tell you if the file is filled in incorrectly.
3. Mark the compatible game versions, add a changelog, save.
4. In the mod description, change **Status** from `Draft` to `Published`.

Before publishing, check the following in `modinfo.json` (the packaging script warns
about missing authors):

| Field | Current value | Why it matters |
|---|---|---|
| `authors` | `["zorolin"]` | without an author the mod page looks empty |
| `name`, `description` | descriptive, in English | this is what the player sees; ModDB requires an English description that clearly explains what the mod does |
| `dependencies.game` | `1.22.7` | the game version the mod was actually tested with (SemVer comparison) |
| `website` | none | optional: a link to the repository or a forum thread |
| `modicon.png` | 480×480 | the icon in the mod list (the game looks for it at the mod root); the picture for the ModDB page is uploaded separately in the web interface |

Separately: a mod must have a **license** (in this repository — `LICENSE`, MIT).
ModDB's terms require the mod license to be compatible with Anego Studios'
terms, and “no license” means “all rights reserved”. The description must also
state that the mod is intended for your own servers or for servers whose owner
has given permission.

`side` for `vsfullcapture` is `Client`: capture happens only on the client; there is
nothing to save on the server (it already owns that data). In singleplayer the mod does
not start capture (`OnlyOnMultiplayer`).

Sources: [guide “Upload your mod to the ModDB”](https://www.vintagestory.at/forums/topic/18633-upload-your-mod-to-the-moddb/),
[wiki: Modinfo](https://wiki.vintagestory.at/index.php?title=Modding:Modinfo).
