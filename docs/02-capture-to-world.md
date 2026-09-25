# 2. Full path: server → capture → `.vcdbs` → local world

A step-by-step guide for the **VsFullCapture** (Harmony mod) +
**vsfullcapture-writer** (offline writer) pair. The result is a real world file
that opens in the game, not just a PNG preview.

The rules this rests on were verified against the 1.22.7 decompile. Two of them
matter most: a `mapchunk` row is mandatory for every column (without it the game
silently ignores the `chunk` rows and regenerates those columns), and block ids are
resolved through `gamedata.ModData["BlockIDs"]` or translated directly in the
palettes (see §2.4).

---

## 2.1. What is captured from the server

The mod patches **one** function — `SystemNetworkProcess.ProcessInBackground(Packet_Server)` —
through which every packet passes. From there it takes everything the server
sends to the client at all:

| Packet | What's inside | Where it goes |
|---|---|---|
| `ServerIdentification` (1) | game version, `MapSizeX/Y/Z`, seed, mod list, `RequireRemapping` | world parameters, mod-set mismatch check |
| `LevelInitialize` (4) | chunk/region sizes, view distance | world parameters |
| `WorldMetaData` (21) | sea level, light levels, world configuration | metadata |
| `ServerAssets` (19) | `Packet_BlockType[]` with `BlockId` and `Code` | **server block registry** → block id translation |
| `Chunks` (10) | blocks, light, `lightSat`, liquids, `lightPositions`, decor, **block entities**, moddata, `Compver` | `chunk` table rows |
| `BlockEntities` (48) | block entity updates outside the chunk (interaction rollback) | the same `chunk` rows, by position |
| `MapChunk` (17) | `RainHeightMap`, `TerrainHeightMap`, `Ymax` | `mapchunk` table rows (**mandatory**) |
| `MapRegion` (42) | terrain, climate, ores | **not saved**: the game rebuilds regions from the seed itself |

Nothing is lost: the block/light/liquid blobs go into the savegame **byte for
byte**, without re-encoding (proven by the offline probe, `./probe-vcdbs.sh`).

**Block entities are mandatory for chiseled blocks.** The shape and materials of
a "chiseled" block live not in the block but in the block entity (`materials`,
`cuboids`, `decorIds`), and the materials are recorded as **server block ids**.
When the world is assembled they are translated into **codes**
(`BlockEntityMicroBlock.MaterialIdsFromAttributes` handles that form too), and
`decorIds` — via the id map. Without this, chiseled blocks and everything drawn
by a block entity (chests, machines, shelves) stay empty.

## 2.2. How the capture works

* The patch runs on the **background network thread**, so it only enqueues a task
  — there is no serialization or disk access there.
* A separate writer thread serializes the packet and appends the record to
  `capture.vscap` (the [vscapture](#25-capture-format-vscapture) format below).
* The file is append-only and survives an abnormal game shutdown: a truncated
  record is ignored, and everything before it stays intact.

## 2.3. Order of operations

### Step 1. Build and install the mod

```bash
export VINTAGE_STORY=/path/to/VintageStory
INSTALL=1 ./build-mod.sh        # builds the mod and puts it into <VintagestoryData>/Mods
```

If the mod needs to be handed to others (or published on ModDB), there is a
separate step: `./package-mod.sh` builds a zip following the game's mod loader
rules and validates it with a real `ModContainer` — see
[01-project-setup.md § 1.8](01-project-setup.md#18-packaging-a-mod-for-publication-moddb).

The `vsfullcapture` mod is installed **on the client only** and requires nothing
from the server. `modinfo.json` has `"side": "Client"` — in that case the engine
does not create the mod's systems on the server side at all
(`ModContainer.InstantiateModSystems`:
`if (!Info.Side.Is(side)) { Status = ModStatus.Unused; return; }`), and the server
does not announce the mod to clients or wait for it
(`ServerMain.CreatePacketIdentification` filters out `Info.Side.IsUniversal()`).

A side benefit: client mods do not end up in the list the client sends to the
server on connect (`SystemModHandler`: `list4` is built only from
`Side == Universal`). So a version conflict is impossible too, when the server
happens to have a mod with the same id but a different version — in that case a
Universal mod would be silently disabled on the client.

### Step 2. Play on the server

Capture runs automatically. To check and flush the buffers:

```
.fullcapture status     how many records and what is in the queue
.fullcapture flush      flush to disk
.fullcapture where      show the capture directory
.fullcapture registry   show/refresh the local block registry snapshot
```

Capture directory: `<VintagestoryData>/FullCapture/<world identifier>/`.

Settings: `<VintagestoryData>/ModConfig/vsfullcapture.json` — you can disable
layers (`CaptureChunks`, `CaptureMapChunks`, `CaptureServerAssets`) and change
the queue depth.

> **A note on column readiness.** The game reads `chunk` rows only when the
> `mapchunk` row for that column exists and says "worldgen is complete". So walk
> around the places you care about in such a way that the client has time to
> receive the column in full, and do not disable `CaptureMapChunks`.

### Step 3. Build the world

**A template world is no longer needed.** The mod builds the savegame itself:
`gamedata` is built from the captured world parameters (seed, sizes, world
configuration), and the `chunk` and `mapchunk` rows — from the chunks. The build
starts either automatically when you leave the world, or via the command:

```
.fullcapture build                     auto-name: "Captured <part of the identifier>"
.fullcapture build My server           a name of your own
```

The result is `<VintagestoryData>/Saves/<name>.vcdbs`, and it appears
immediately in the singleplayer world list. The build runs in a background
thread, and progress is written to the log.

Auto-build is enabled by the `BuildOnLeftWorld` setting (enabled by default) and
does not trigger if there are fewer complete columns than `MinColumnsToBuild`.

**During the build, block ids are translated into the local numbering.** Chunk
blobs store the server's ids, while your world has its own numbering — the
divergence starts already at id 2 (on the server that is `multiblock-…`, on the
client `meta-filler`). The translation is done by editing the **palette** right
inside the blob: the bit planes store palette indices, so only the palette values
change, not the 32768 blocks. This requires a **local block registry** — "id in
this world → code":

* the mod takes it itself in singleplayer (`.fullcapture registry`,
  `<VintagestoryData>/FullCapture/localregistry.json`);
* you can point to your own file with the `LocalRegistryPath` setting;
* in an offline build — `--local-registry <file>` or
  `--local-registry-from <world.vcdbs>`.

If there is no registry, the mod writes in `BlockIDs` (the old path): on
`AssetsFirstLoaded` the game reassigns live blocks to the server ids. This works,
but on every world open it runs the whole registry through the remapper and turns
missing blocks into placeholder blocks. That is why, when ids are translated,
`BlockIDs` is **not written** — otherwise the remapper would shift the blocks a
second time, on top of the already translated ones.

### Step 4. Verify (optional)

The same thing can be done outside the game, with the tool — for example, if you
want to see the result first:

```bash
./probe-vcdbs.sh                                    # builds the tools
CLI=tools/VsVcdbsWriter/bin/Release/net10.0/vsfullcapture-writer.dll

dotnet $CLI info   <capture directory>              # seed, world sizes, column readiness
dotnet $CLI registry <world.vcdbs> -o local.json    # local registry from the savegame
dotnet $CLI build  <capture directory> --local-registry local.json -o out.vcdbs
dotnet $CLI verify out.vcdbs                        # structure: mapchunk, column completeness
dotnet $CLI check  <capture directory> out.vcdbs --local-registry local.json   # block codes
```

The writer **does not overwrite an existing world** without `--force`, and builds
into a temporary file `<out>.building`, which is moved into place only after a
successful write. In addition, it refuses to work if the capture has no
identification packet (the world sizes are unknown) or has no complete column
with a heightmap — in those cases the existing file is left untouched.

`check` verifies the id translation specifically: for every palette entry, both
sides must be the same block. On a real capture it passes with no mismatches; if
you build a world without translation, it immediately shows shuffled blocks.
Note: the command does not validate `--local-registry` itself — with the wrong
registry, both `build` and `check` will agree on the wrong blocks.

`verify` reads the rows with the game's own class and checks the main thing:
**every column with chunks has a `mapchunk` row**. Without it the game silently
regenerates the column and our blocks disappear. It also parses a sample of
chunks through the client path and counts non-empty blocks.

Building with `--template <world.vcdbs>` is supported too — then `gamedata` and
`mapregion` are taken from a finished world instead of being created from
scratch.

### Step 5. Open the world in the game

The assembled world will appear in the world list. Buildings are in their places:
the terrain is generated from the same seed, and the blocks are supplied from the
capture.

**The player spawns in the center of the captured area** — the new world's spawn
point is set automatically.

---

## 2.4. If the blocks look wrong

First look at which path the world was built by — from the `block ids translated`
line in the build log and from the presence of `BlockIDs`:

* **ids translated (palettes rewritten), `BlockIDs` not written.** The world does
  not depend on the remapper: blocks are read in the local numbering. You can
  verify with `check` (see step 4) — it compares the block codes against the
  capture.
  * Blocks that are not present locally become air. Exactly how many is visible
    in the `into the fallback block N` line and in `check`. You can replace them
    with another block using the `--missing-block <code>` option / the
    `MissingBlockCode` setting.
  * If the local registry was taken from the wrong mod set (mods were added after
    the snapshot), the ids will diverge. The snapshot is refreshed on every entry
    into a singleplayer world.
* **No translation was done, `BlockIDs` was written** — the game was asked to
  reassign the blocks with the remapper:
  * the mod set matches — the ids will match, everything as on the server;
  * the mod set differs — blocks that are not present locally become `IsMissing`
    placeholder blocks (fixed with `/bir map|remap` or `config/remaps.json`);
  * **this path runs the whole registry through the remapper on every world
    open** — which is exactly why the main path is now translation in the data.

The `RequireRemapping` field from the identification packet shows whether the
server itself considers its mod set non-standard.

**Why the local registry comes from singleplayer.** In multiplayer the client
does not build the registry itself: it receives the block list from the server
and lays them out by the ids sent by the server
(`ClientSystemStartup.LoadBlockTypes`). That is, in a session on a server the
client holds the server's registry, and there is nothing to translate by it. Your
own registry is visible where a local server runs — in singleplayer. The snapshot
is taken only for "dense" numbering with no gaps and no placeholder blocks: a
world that was opened with someone else's ids has its numbering shuffled by the
remapper.

---

## 2.5. Capture format `vscapture`

An append-only container of the game's raw protobuf messages. The implementation
is `src/VsChunkDump.Core/CaptureFormat.cs`, read both by the mod and by the tool.

```
File header (16 bytes): "VSCP" | u16 version | u16 headerSize | u32 | u32

Record (20 bytes + payload):
  0  u32 magic "VSRC"
  4  u16 record type  (1 identification, 2 world parameters, 3 metadata,
                       4 assets, 5 chunk, 6 heightmap, 7 region,
                       8 block entity update)
  6  u16 flags        (bit 0 = payload compressed with Brotli)
  8  u32 payload length
  12 u32 uncompressed length
  16 u32 CRC-32 (IEEE) of the payload
  20 ...  payload
```

Records larger than 4 KiB are compressed with Brotli. Chunks are written **one at
a time**: a single corrupt fragment does not lose the whole batch, and the reader
applies "last record wins" for chunks that are sent again.

There is also a simpler dump format from `VsChunkDump.Core` (`manifest.json` +
`dim<N>/r.*.vscr`, the CLI commands `demo` / `render-*` / `inspect`): it is more
compact and suitable for PNG previews, but does not produce a world in the game.
The mod does not write it — for a local world only `vscapture` is used.

---

## 2.6. Known limitations

**What the capture does NOT save** (and why):

| Not saved | Reason |
|---|---|
| entities from chunks (mobs, dropped items, item frames) | over the network an entity goes out in "client" form (`ServerPackets.getEntityDataForClient`), while the savegame stores `Entity.ToBytes(forClient: false)` — these are different layouts, and the second cannot be rebuilt from the first without a live object |
| the contents of `mapregion` | the game rebuilds regions from the seed itself; they are not needed at load time |
| `Structures` and per-column `Moddata` from `mapchunk` | only `ChunkX/Z`, `Ymax`, `RainHeightMap` and `TerrainHeightMap` are carried into the capture; the rest will not be in the assembled world |
| the column's rock map (`TopRockIdMap`, `SedimentaryThicknessMap`, `CaveHeightDistort`) | these arrays never reach the client; the writer fills them with zeros, so the rocks and sedimentary layer in the downloaded world may differ from the server |
| the server's chunk format version | `Compver` is captured, but the current version (2) is written to the savegame; a capture taken by a different game version is not checked against it automatically |

**The server's mod list is not always saved.** The patch is installed in
`ModSystem.Start`, that is, before connecting, and the `ServerIdentification`
packet often does make it into the capture anyway (a real capture has several such
records — one per connection). But if the packet did slip past the patch, the mod
at the first opportunity takes `MapSize`, the seed and the world identifier from
`capi.World` and writes them as a separate record — then the capture has no server
name, no mod list and no `RequireRemapping` flag. This does not affect the world
build: the block registry arrives in a separate `ServerAssets` packet.

**Block entities are saved** (including chiseled blocks, chests, machines,
shelves): their data in the savegame is "class name + TreeAttribute", exactly the
same as what arrives over the network. Old captures (taken before block entity
support) do not contain it — in such a world the chiseled blocks will be empty,
and a new capture is needed.

**A build without a template relies on the world configuration from the
capture.** If the `WorldMetaData` packet did not make it into the capture (for
example, it was taken by an old mod version), the world will be built with
default generation settings: the seed and height will be correct (they are stored
in `gamedata`, and `WorldConfig.loadFromSavegame` takes `MapsizeY` from there),
but the terrain and climate parameters around the captured area will differ from
the server. Inside the captured area the blocks are ours anyway.

**One capture directory covers every connection ever made.** The mod picks the
directory once when the client starts and appends to it every session, so a
single file can hold data from several servers. For all chunks the writer takes
the block registry and world configuration of the **last** record, so data from
earlier sessions may be translated incorrectly. For now this is a limitation: for
a clean result, capture into a separate directory (`CaptureName` in the settings)
for a specific server.

**`ServerChunk.FromBytes` requires a real world.** It resolves decor and block
entities through the world, and with `null` it throws `NullReferenceException`.
The tool therefore validates the data through the client path
(`ClientChunk.CreateNewCompressed`), which does not need a world. If you write
your own code around `.vcdbs` — keep this in mind.

## 2.7. What is verified and what is not

The capture format, the palette translation, the `mapchunk`/complete-column gate rules
and the archive layout have all been checked against the game — by the test suite, by
running the game's own code offline (`./probe-vcdbs.sh`), and in a live client. The full
list, including what is **still** unverified, is in
[03-verification.md](03-verification.md).
