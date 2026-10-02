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
| `Calendar` (13) | world age, hours per day, month length, time speed and its modifiers | calendar settings of the assembled world (see below) |
| `CalendarUpdate` (83) | nothing but the two clock values | **not saved**: the client's own calendar already tracks that clock |
| `MapRegion` (42) | terrain, climate, ores | **not saved**: the game rebuilds regions from the seed itself |

Nothing is lost: the block/light/liquid blobs go into the savegame **byte for
byte**, without re-encoding (proven by the offline probe, `./probe-vcdbs.sh`).

**Entities (mobs, dropped items, item frames) are taken from the live objects**, not
from a packet: over the network an entity travels in "sync" form
(`Entity.FromBytes(reader, isSync: true)`), from which the savegame form cannot be
assembled, and the mod holds the entity itself. The events `OnEntitySpawn` /
`OnEntityLoaded` give it right after initialization, `OnEntityDespawn` removes what
was destroyed, and a periodic pass over `LoadedEntities` refreshes the positions —
those come over UDP (`Packet_UdpPacket.BulkPositions`), which the patched method never
sees. In the savegame an entity is stored inside its chunk row, exactly like a block
entity ("class name + `Entity.ToBytes(forClient: false)`").

**The world state — the clock and the player position — is taken from the live client
as well**, and it is what makes the assembled world open at the captured moment instead
of at its own midnight:

* the **clock** is read from `capi.World.Calendar`. That is the same clock the server
  saves, only read later: the client is set from the server's calendar packet and then
  advances it locally between packets, so its value is the freshest view of it. The time
  of day, the day of the year and the season are all derived by the game from this one
  number;
* the **calendar settings** (world age, hours per day, month length, time speed and its
  modifiers) come from the `Calendar` packet, because they are what turns a clock into a
  date: the same clock in a world with a different day length is a different day of the
  year. The packet packs its floats into ints, so they are unpacked with the game's own
  `CollectibleNet` helpers — reading the packed ints raw would give a 200000-hour day;
* the **player position and facing** come from the live player entity.

The build writes the clock into `gamedata.TotalGameSeconds` (`TotalSecondsStart` with it)
and the position into `DefaultSpawn` — see step 5 below.

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
* Entities and the world state are the exception: their data has to be read from the
  live client objects, so it is taken on the **main thread** — the entity events fire
  there, and the clock and the player position are read from the client API on the game
  tick. Only the ready payload goes to the writer thread.
* A separate writer thread serializes the packet and appends the record to
  `capture.vscap` (the [vscapture](#25-capture-format-vscapture) format below).
* The file is append-only and survives an abnormal game shutdown: a truncated
  record is ignored, and everything before it stays intact.
* Before a build the writer queue is drained (`WaitForQueue`), so records still in
  flight are in the file the build reads.

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
layers (`CaptureChunks`, `CaptureMapChunks`, `CaptureServerAssets`, `CaptureEntities`,
`CaptureWorldState`) and change the queue depth. `CaptureEntityRefreshIntervalMs`
(30 s by default) is how often the state of the loaded entities is written again, `0`
leaves the spawn-time state only; `CaptureEntitiesPerTick` spreads that pass over
several game ticks. `WorldStateIntervalMs` (30 s by default) is how often the clock and
the player position are written — the state itself is re-read every tick and the last
value goes in when you leave the world or start a build, so the interval only bounds
what a crash mid-session would cost.

`.fullcapture status` shows the state as captured — the date, the season, the position
and whether the calendar settings came from the server's packet.

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
thread, and progress is written to the log. **How it ended is also said in chat and
appended to `<capture directory>/build.log`** (one line per build: `OK`, `SKIP` or
`FAIL` plus the reason) — a build happens after you have left the world, and a build
that fails every time must not look like a build that works.

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

Whatever the numbering, the world **must carry the `BlockIDs` table that describes
it** — the game's `ServerSystemBlockIdRemapper` reads it on `AssetsFirstLoaded` and
from it learns what the ids in the chunks mean, then moves its own live blocks to
those ids. So:

* ids translated — the table written is the **target registry** (the one from
  `--local-registry`), because that is the numbering the chunks now use;
* ids not translated — the table written is the capture's **server registry**.

The table and the chunks cannot be out of step, and an empty table is not "no
remapping needed": with an empty table the remapper has nothing to learn from, the
server keeps its own numbering, and every chunk is read with the blocks shifted —
the world is then full of wrong blocks, and a block entity whose class reads its
block (`BlockEntityCage`, for one) throws on the client. That is exactly what an
empty table looks like in practice: `BlockIDs` is written by the game on the next
open, so the file ends up looking innocent.

**A block entity is written only with its block.** The data of an entity names its
own block (`blockCode`) and its position; the game puts that block into the entity,
and on the client it looks the block up **by position alone**
(`ClientSystemEntities.UpdateBlockEntityData` calls `GetBlockRaw(posx, posy, posz)`).
If the block under the entity is something else — the block was removed or replaced
after the entity was captured — the entity is stale: the game deletes such entities
on load with a warning (`BETransient`), and a class that reads its block without
checking throws on the client (that is how a `BlockEntityCage` NullReference once
took the game down). The builder therefore checks every entity against the block id
at its position, read from the chunk palette the way the game reads it, and skips the
ones that do not match. The build summary prints how many:
`dropped N, of them M not on their block`.

### Step 4. Verify (optional)

The same thing can be done outside the game, with the tool — for example, if you
want to see the result first:

```bash
./probe-vcdbs.sh                                    # builds the tools
CLI=tools/VsVcdbsWriter/bin/Release/net10.0/vsfullcapture-writer.dll

dotnet $CLI info   <capture directory>              # seed, world sizes, column readiness, recorded clock and player
dotnet $CLI be     <world.vcdbs>                    # every block entity against the block under it, class by class
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

The same run states what the world will open with — this is the way to check the
time and the spawn point without launching the game:

```
World state: clock 47,850,229s, world age 517.5 days, 24h day, spawn 511887, 172, 511855 (yaw 7.79)
playerdata rows: 0 — the character is created at the spawn point
Entities: 0 in 0 chunks of the sample
  The sampled chunks carry none, but the world has 5,105 in 294 chunk(s) — the sample missed them.
```

Two of those lines exist because the first answer they gave was wrong. The entity
count in the sample is not the entity count of the world: a sample of 200 chunks out
of 76,536 misses them easily, and the message used to blame the capture for a build
that did support entities. When the sample is empty `verify` now walks the whole file
and says which of the two it is. The `playerdata` count decides whether the spawn
point applies at all: a built world is a new file with no characters in it, so
`DefaultSpawn` is what the game uses — but if the world was ever entered before a
rebuild, this line is where that shows.

Building with `--template <world.vcdbs>` is supported too — then `gamedata` and
`mapregion` are taken from a finished world instead of being created from
scratch.

### Step 5. Open the world in the game

The assembled world will appear in the world list. Buildings are in their places:
the terrain is generated from the same seed, and the blocks are supplied from the
capture.

**The world opens at the captured moment, with the player at the captured place** —
so verifying a mob means walking to it, not `/time set` and `/tp`:

* the clock goes into `gamedata.TotalGameSeconds`, and the game derives the time of day,
  the day of the year and the season from it. With the captured clock the assembled world
  opens at the same time of day and in the same season as the server did;
* `TotalSecondsStart` goes in with it, so the world **age** is the source world's age
  rather than zero — which is also what schedules temporal storms;
* the spawn point is `SaveGame.DefaultSpawn`, set to the captured player position and
  facing. This is where the character is created, so a fresh save puts you there;
* the build log states both, so you can check them before opening the world:

  ```
  Time: July 4, 1387, 07:30 (clock 3423600s, world age 3.3 days, 24h day, 9-day months, from the server calendar)
  Spawn point: 123, 72, -456 — where the player stood (yaw 1.57)
  ```

* if the capture has no player position, or the position does not fit the world being
  built, the spawn point stays the centre of the captured area and the log says why;
* the game reads only `y` and `yaw` from a spawn point: the pitch is overwritten with its
  own value, so it is not recorded at all. And `y = 0` means "drop me on the terrain
  surface" to the game — a player position is never that, so a recorded height is always
  the player's own.

---

## 2.4. If the blocks look wrong

First look at which path the world was built by — the `block ids translated` line in
the build log says whether the palettes were rewritten, and `BlockIDs written` says
which table went into the save:

* **ids translated (palettes rewritten), `BlockIDs` = the target registry.** The
  chunks and the table are in the same numbering, and the remapper renumbers the
  live blocks to it. Verify with `check` (step 4) — it compares the block codes
  against the capture — and with `be`, which checks every block entity against the
  block under it.
  * Blocks that are not present in this world become the fallback block, air by
    default. How many is in the `into the fallback block N` line; you can replace
    them with another block using `--missing-block <code>` / `MissingBlockCode`.
  * If the registry was taken from the wrong mod set (mods changed after the
    snapshot), those blocks land in the fallback — and their block entities are
    skipped (see below), so nothing is left hanging.
  * The snapshot is refreshed on every entry into a singleplayer world.
* **No translation was done, `BlockIDs` = the capture's server registry** — the
  numbering of the chunks is the server's:
  * the mod set matches — the remap is a no-op, everything as on the server;
  * the mod set differs — blocks that are not present locally become `IsMissing`
    placeholder blocks (fixed with `/bir map|remap` or `config/remaps.json`).
* **`BlockIDs` is empty or missing** — nothing can work: the game knows nothing
  about the numbering of the chunks and reads them with its own. The builder never
  produces such a world (it warns instead), but a world from before this rule can
  be in that state.

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
                       8 block entity update, 9 entity, 10 entity despawn,
                       11 world state — clock and player)
  6  u16 flags        (bit 0 = payload compressed with Brotli)
  8  u32 payload length
  12 u32 uncompressed length
  16 u32 CRC-32 (IEEE) of the payload
  20 ...  payload
```

Records larger than 4 KiB are compressed with Brotli. Chunks are written **one at
a time**: a single corrupt fragment does not lose the whole batch, and the reader
applies "last record wins" for chunks that are sent again. Entities follow the same
rule: an entity record replaces the previous state of that id, and an entity despawn
record removes it — so the capture ends up holding the world as it was when you left.
The world state follows the same rule: it is re-written while the world runs, and the
build uses the last record in the file.

There is also a simpler dump format from `VsChunkDump.Core` (`manifest.json` +
`dim<N>/r.*.vscr`, the CLI commands `demo` / `render-*` / `inspect`): it is more
compact and suitable for PNG previews, but does not produce a world in the game.
The mod does not write it — for a local world only `vscapture` is used.

### A damaged capture does not stop the build

The file is appended to across game runs, so the only damage it can collect is a
record cut short by a game that was killed while writing it. Such a stub stays in
the **middle** of the file forever — everything after it was appended later — and a
reader that gives up on a checksum error would then fail every build from that day
on, silently.

Two rules follow, and both are in `CaptureFormat.cs`:

* **`CaptureReader` steps over damage.** On a record whose body does not match its
  CRC (or whose header is missing), it looks for the next offset that starts three
  intact records in a row and continues there. The records in the gap are lost —
  usually one chunk — and the gap is reported (`CaptureReadIssue`); everything after
  it is read as usual. A file that merely lost its tail reports an incomplete tail and
  stops, which is not a gap.
* **`CaptureWriter` trims an incomplete tail when it opens the file.** It searches
  the last 64 MiB from the end for the last record whose body is complete and whose
  CRC matches — that is the file's real end — and drops whatever follows it. A clean
  file is not touched. Without this, the next run would append after the stub and turn
  a removed tail into permanent mid-file damage.

The build says what it stepped over: `vsfullcapture-writer info` prints a `Damage:`
line, the mod writes the gaps into the log and into
`<capture directory>/build.log`, and the same line goes to chat.

---

## 2.6. Known limitations

**What the capture does NOT save** (and why):

| Not saved | Reason |
|---|---|
| the server side of an entity (`Attributes`) | the client only receives the synced part (`WatchedAttributes`), so in the assembled world the health, the inventory of a dropped item and the model state carry over, while the server behaviour state (AI path, spawner link, task) starts from defaults. The mob itself is there and behaves normally — it just does not remember what it was doing |
| entities outside the client's simulation range | the client never receives them: what was not seen during the capture is not in the world |
| the contents of `mapregion` | the game rebuilds regions from the seed itself; they are not needed at load time |
| `Structures` and per-column `Moddata` from `mapchunk` | only `ChunkX/Z`, `Ymax`, `RainHeightMap` and `TerrainHeightMap` are carried into the capture; the rest will not be in the assembled world |
| the column's rock map (`TopRockIdMap`, `SedimentaryThicknessMap`, `CaveHeightDistort`) | these arrays never reach the client; the writer fills them with zeros, so the rocks and sedimentary layer in the downloaded world may differ from the server |
| the server's chunk format version | `Compver` is captured, but the current version (2) is written to the savegame; a capture taken by a different game version is not checked against it automatically |

**Entities are saved** (mobs, dropped items, item frames): the mod takes them from the
live objects, in the same savegame form a chunk row stores them in. What this means in
practice:

* an entity is written into the chunk its position falls into, so entities of a column
  that was **not** captured in full (or has no heightmap) are not written — the build
  reports how many and why;
* a despawn is only honoured when it is final (death, combustion, pickup, expiry,
  removal). "Out of range", "chunk unloaded" and "last player disconnected" mean the
  client merely stopped tracking the entity — the capture counts those separately instead
  of writing a removal (see the next section for what the build does with them);
* the position is the one from the **last refresh** before you left (30 s by default),
  so a mob that was running may stand up to that much behind where it actually was;
* the entity set of the world is scoped **per chunk**, not unioned over the whole capture:
  an entity is left out only when the place it stands in was looked at again later and it
  was not there (see the section below). An entity that died outside the client's view never
  produced a removal record, but a place the client never returned to is not evidence that
  its mobs are gone, so nothing there is erased;
* entity ids come from the server; the build raises `gamedata.LastEntityId` above them,
  so newly spawned mobs get new ids instead of colliding with the captured ones;
* of the vanilla classes only `EntityAgent`, `EntityItem`, `EntityChunky`,
  `EntityHumanoid` and `EntityPlayer` are registered by the game itself. A **modded**
  entity class is written only if the mod registers it on the client too
  (`api.RegisterEntity`); otherwise the entity is skipped with a warning in the log.

Old captures (taken before entity support) contain none — in such a world there will be
no mobs, and a new capture is needed. The build log prints
`Entities written: N of M`, and the offline `verify` prints an entity summary. That
summary reads the class names and counts them; it cannot *instantiate* the vanilla mob
subclasses (`EntityDrifter` and the rest), because the game registers those from its own
mods at runtime. `verify` reports them as `not instantiable offline` — the names and
positions are still read, and the game resolves the classes from its own registry when
the world opens.

### The entity set is scoped per chunk, not to the newest session

The capture is appended to across game runs, and **a despawn is only captured for an
entity the client is tracking**. Read flat, the file is therefore a union of everything
the client ever saw, and an entity that died out of view, or while the player was
offline, never produces a removal record — it would stay in the world forever. That is
not a bug in the build, it is the limit of what a client can know.

Cutting the world down to the newest session is not the answer either: it throws away
every mob in a place the player simply did not revisit that evening. The question is not
"how old is this entity" but **when the capture last looked at the particular chunk it
stands in**:

> An entity is left out when the chunk section it stands in was received **again later**,
> in a connection that went on recording entities, and the entity was not among them.

Three things make that sound:

* **the entity half is a real observation, not a guess.** The mod does not wait for
  something to happen to a mob: a periodic pass (30 s by default) rewrites the state of
  *every* entity in `LoadedEntities`, and `OnEntityLoaded` fires for each entity a newly
  received chunk brings in. Once a connection is recording entities at all, the client's
  whole tracked set is written over and over, so a live entity standing in that chunk
  cannot stay silent. What is missing from those records is not there;
* **receiving a chunk is not enough on its own.** A session can load chunks and record no
  entity at all (the player reconnected and left), and "the client said nothing" is not
  "there is nothing". Such a connection supersedes nothing, and its chunks are counted as
  carrying no newer entity information. A chunk also has to have arrived **before** the
  session's last entity record — a chunk loaded on the way out was never looked at with
  entity tracking;
* **silence about a place is not evidence about it.** A chunk that is never received again
  keeps its entities exactly as they were, however old they are.

Measured on a real capture (one server world, 50 connections over six days, 1 October
2026 UTC, 76 536 chunk sections). The offline `info` on the live directory, which applies
the rule without building anything:

```
Entities:               10376
Despawns:               14954 (reason not recorded 8342, Expire 4229, PickedUp 2380, Death 3)
  last seen:            688 in the last connection, 4633 within three, 5055 earlier
  kept by the rule:     4236 of 10376 (6140 superseded by a later look at their chunk)
  never revisited:      67056 chunk section(s) received last by a connection that recorded no entity
```

The build writes **4 236** of 10 376 entities and drops **6 140** — every one of them with
a concrete reason: the player stood in that chunk again later and it was not there.

The other 4 236 are **kept on purpose**: 67 056 chunk sections were last received by a
connection that recorded no entity, so there is no later look to compare against, and the
rule does not invent one. This is the difference from taking the newest connection, which
would have cut the world to 688 entities — and from an earlier version of this rule that
compared only connections, which dropped 8 879 because it counted a chunk received in a
session as "looked at" even when the session ended before the entity pass covered it. A
chunk being loaded is not the same as the client having looked inside.

* since 1.2.4 every entity carries **which connection last mentioned it**; since 1.2.6
  every chunk section carries **when it was last received** and **whether entity records
  followed that receipt**. `info` prints both, and each build line repeats the counts;
* `EntityMaxAgeConnections` in the mod settings (or `--entity-max-age <n>` in the CLI) adds
  an **age bound on top of** the chunk rule, and is off by default (any value ≤ 0). It
  exists for exactly the leftovers above — a capture whose newest sessions never covered
  some chunk. A positive `n` also drops entities no record mentioned for more than `n`
  connections. There is no switch that restores the plain union: the rule only removes
  what the capture itself contradicted;
* the chat status reports the split of despawns the mod itself saw: what was **removed**
  (death, combustion, pickup, expiry, removal) against what was **kept** because the
  client only stopped tracking it (out of range, unloaded, disconnected). The rule above is
  what finally drops those kept ones — but only where the capture has a later look at the
  same chunk.

**The world state is saved** (the clock and the player position). What to know:

* the clock reproduces the time of day, the day of the year and the season as the client
  saw them when the capture was written. The periodic write can lag by
  `WorldStateIntervalMs` (30 s), but `.fullcapture build` forces a fresh snapshot first and
  leaving the world writes the one from the last tick, so the recorded moment is the end
  of the session;
* the calendar settings come from the `Calendar` packet — on connect and then about once
  a minute (`MagicNum.CalendarPacketSecondInterval`). If it was missed, they are read from
  the client's calendar: the day length and the month length are still the server's (the
  client is told both), but the time speed modifiers are assumed to be the default
  `baseline = 60`, because the client API does not expose them. The build log names the
  source it used;
* the season in the log is what the game reported at that position. In the assembled
  world the season is derived by the game from the restored clock and the world
  configuration, which is how the two stay consistent;
* the world's **age** travels with the clock (`TotalGameSecondsStart`), so a long-lived
  server world does not become a brand-new one. That is also what schedules temporal
  storms — a restored world keeps the storm rhythm of the source world;
* a world built with `--template` gets the clock and the spawn point written into the
  copied `gamedata` as well. The template keeps its own world configuration and block
  tables, and keeps its own spawn point when the capture has no player position;
* `DefaultSpawn` applies when the character is created. An already entered world keeps the
  player where its own `playerdata` says — a rebuilt world is a new file and has none.

Old captures (taken before this record) have no world state: such a world opens at its own
midnight and starts the player in the centre of the captured area, exactly as it did
before. `info` prints `World state: none` for those, and the build log warns about it.

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
earlier sessions may be translated incorrectly. Entity records are keyed by id as
well, and those ids are per-server: an entity of the last connection replaces an
entity with the same id from an earlier one. For now this is a limitation: for
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
