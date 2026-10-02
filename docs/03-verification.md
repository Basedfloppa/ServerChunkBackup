# What is verified, and what is not

Everything below was checked against **Vintage Story 1.22.7**. Statements that come
from decompiling the closed-source assembly rather than from public documentation may
change in the next game version.

## By the tests in this repository

`dotnet test VsChunkDump.slnx` — **163 tests**:

- the `vschunkdump` dump format, palette + bit planes, CRC32, deduplication, resume;
- PNG rendering (top view, isometric, slice) and the `demo` generator;
- the `vscapture` capture format: round-trip, Brotli compression, CRC, truncation
  resilience, block entities;
- damaged captures: a record with a flipped byte and a whole wiped record are stepped
  over and the records after them are still read; a capture that lost its tail reports
  an incomplete tail and keeps what is readable; reopening a capture whose last record
  was cut short trims the stub, while a clean capture is left alone;
- entity records: codec round-trip, "last record wins" by id, despawn, and grouping
  by chunk section (including negative coordinates);
- entity liveness: the despawn reason survives the round-trip and a record of the older
  8-byte shape still decodes (with the reason unknown); despawns and their reasons are
  counted; an entity carries the connection that last mentioned it; the optional age bound
  leaves out only what is older than the limit, and keeps entities whose age is unknown;
- the entity scope is **per chunk**, not per session: an entity is left out only when its
  own chunk was received again later in a connection that recorded entities and the entity
  was not among them; an entity in a chunk nobody returned to survives, and a connection
  that received chunks but recorded no entity supersedes nothing (its chunks are reported
  as carrying no newer entity information);
- world state records: codec round-trip (clock, calendar settings, player position and
  facing), "last record wins", a record written without the tail fields still decoding,
  and a capture without one still loading;
- the `vsblockregistry` format and palette translation by block code.

The mod also compiles against the real `VintagestoryLib` 1.22.7 + Harmony 2.4.2 with
0 warnings.

## By running the game's code offline

`./probe-vcdbs.sh` executes the game's own classes without launching the game:

- block and liquid blobs taken from the wire packet match the savegame blobs
  **byte for byte**;
- `ServerChunk.ToBytes()` → `FromBytes()` preserves blocks;
- a `.vcdbs` written by the writer is read back by the game's `SQLiteDbConnectionv2`,
  and the `ChunkPos` key is correct;
- palette id translation was confirmed on a real capture (6,276 chunks, 4,297 palettes,
  1,082 of them compressed) with the `check` command;
- chiseled-block materials translated into codes — `chisel` on a real world
  (114 block entities, 125 materials, 0 mismatches);
- a built archive is unpacked and parsed by the game's real `ModContainer`
  (`VsVcdbsProbe modzip`), including five deliberately malformed archives.

`VsVcdbsWriter selftest` runs the whole path end to end: synthetic game packets →
capture → `.vcdbs` → comparison of blocks, translated ids, block entities and
entities. The entity part is checked with the game's own code: the blob field 5/6
of the chunk row is read back through `ServerChunk`, and every entry is instantiated
and parsed with `ClassRegistry.CreateEntity` + `Entity.FromBytes` — the same calls
`ServerChunk.AfterDeserialization` makes. The check covers the class name, the type
code, the position and the attributes tree (the health value written at capture time),
that a captured despawn is absent from the result, and that `LastEntityId` in
`gamedata` is raised above the captured ids. The synthetic capture carries **two
connections and two chunks**: section 2 is sent again in the second connection, so the
entity written there in the first one is asserted to be **absent** from the world (the
later look at its chunk supersedes it), while an entity in section 3, which is never sent
again, is asserted to be **present** — the second connection says nothing about that
place.

Two negative controls back that up (`tmp/negative-controls-entity-scope.sh`): disabling
the chunk rule makes the superseded entity reach the world, and removing the
no-information guard makes the unit test fail. Both controls restore the file and the
result is green again — a control that stayed green would prove nothing.

The world state is checked in the same run, on **both** build paths (with and without a
template): the clock and the calendar settings are put into `gamedata` field by field,
including the unpacking of the packet's int-packed floats — read raw, a 20-hour day would
come out as 200000 — and the spawn point is checked to be the floor of the captured player
position with its facing. The synthetic record is built through the shared `PacketMapping`
from a real `Packet_ServerCalendar`, so the packing is on the tested path.

**Our own layer reader is checked against the game's.** The builder needs to know which
block id lies at a position (to refuse an entity whose block is not under it), so
`CombinedLayerBlob.TryReadBlockId` decodes the bit planes itself. `verify` compares it
with the game's `IChunkBlocks.GetBlockId` position by position: on three real chunks all
32,768 positions per chunk agreed (`OK: our layer reader matches the game in all 32768
positions of a chunk`). The bit-plane layout is `floor(log2(palette))` planes of
`int[1024]`, word `index / 32`, bit `index % 32`, plane `n` at weight `1 << n`
(decompiled `ChunkData.UnpackBlocksTo`).

**A block entity is checked against the block under it.** `vsfullcapture-writer be`
reads every entity in a built world, takes the block it declares (`blockCode`) and
the entity's position, and looks up the block in the chunk at
`((y % 32) * 32 + z % 32) * 32 + x % 32` — the same formula the game uses. On the
test machine's world it found 2,626 of 129,138 entities sitting on another block (the
game deletes those on load: `BETransient` prints "Will delete BE"). That check is what
found the client crash below.

**A built world with an empty `BlockIDs` table is unreadable — confirmed on real data.**
The world of the test machine was built with translated ids and, at that time, without
the table (the builder refused to write it, believing the remapper would shift the
blocks twice). The game opened it, its `ServerSystemBlockIdRemapper` found no stored
table, kept its own numbering and wrote that table into the save — 37,426 entries,
while the chunks were numbered by the 17,993-block client registry. Every block came
out wrong, and the client crashed with

```
System.NullReferenceException
  at CatchAndRelease_Lib.BlockEntityCage.get_DisplayEndIndex()
  at CatchAndRelease_Lib.BlockEntityCage.FromTreeAttributes(...)
  at Vintagestory.Client.NoObf.ClientSystemEntities.createBlockEntityFromPacket(...)
```

— the packet path takes the block at the entity's position (`GetBlockRaw(posx, posy,
posz)`), and at that position there was no cage. The same world built **in the server's
numbering with the capture's registry** has both `crlib.Cage` entities on their own
blocks and opens consistently. Decompiled evidence: `ServerChunk.AfterDeserialization`
resolves the entity's block by `blockCode` (falling back to the position), the client
by position only, and `ServerSystemBlockIdRemapper.RemapBlocks` learns the numbering of
the world exclusively from `gamedata.ModData["BlockIDs"]`.

**A real damaged capture was read end to end.** The 4.6 GiB capture of the test machine's
server world has two records whose bodies were lost when the game was killed mid-write
(the second one ends in the middle of a Zstd frame). `info` walks the whole file, reports
`Damage: 2 region(s) skipped, 0.4 KiB lost`, and still loads 3,496,774 records, 76,536
chunks, 5,112 entities and the last world state (`14. July, Year 1, 19:43, Summer`, player
at 511887.8, 172.0, 511855.3). Before this, the same file made **every** build fail with
`CRC mismatch … @ 3238997276`, which is what the mod reported only in the log.

> `modzip` checks **the archive structure and `modinfo.json` parsing** only. It does
> not load the assembly at runtime, so a crash in `ModSystem` or an incompatibility
> with the game version cannot be caught this way.

## In the game

The mod ran in a real 1.22.7 client: the Harmony patch on
`SystemNetworkProcess.ProcessInBackground` installed, auto-build assembled a world
from a real capture (122,119 records, 7,752 chunks, 646 columns), and `verify` of the
result passed — 200/200 chunks, 99.5 % of blocks non-empty.

That same client then produced the case the reader had to be fixed for. Three
auto-builds in one evening failed, each identically, and the only trace was one line
in `client-main.log`:

```
1.10.2026 20:50:25 [Error] [serverchunkbackup] Failed to build the world:
  System.IO.InvalidDataException: CRC mismatch in .../capture.vscap @ 3238997276
     at VsFullCapture.AutoWorldBuilder.Run(String captureRoot, String worldName)
```

The world file in `Saves/` was meanwhile still being rewritten by the game itself
(85,656 chunk rows and a `playerdata` row — our builder writes none), which is why the
world kept opening with the old clock, the old spawn point and no entities: **the
rebuild never got past the first damaged record.** The reader now steps over the damage,
and the same capture builds: 76,536 chunks, 5,105 entities, `Time: 14. July, Year 1,
19:43, Summer (clock 47850229s, world age 517.5 days)`, `Spawn point: 511887, 172,
511855 — where the player stood (yaw 7.79)`, with `verify` reading 5,105 entities in
294 chunks back out of the written world. (5,105 was the union, before the entity scope
existed; the same capture now yields 4,293 — see the per-chunk rule in
[02-capture-to-world.md](02-capture-to-world.md#the-entity-set-is-scoped-per-chunk-not-to-the-newest-session).)

The `build.log` / chat report of how a build ended is part of 1.2.1: the version that
produced those three failures (1.2.0) left the reason in the log alone, so a build that
failed every time was indistinguishable from one that worked unless the log was read.

## Still needs checking

- that a built world **opens and looks correct** — only the structural `verify` passes.
  The worlds built before this rule did not open: see the empty-`BlockIDs` finding above.
  A world built in the server's numbering (no `--local-registry`) has passed the entity
  check and the block-entity check, but has not been opened in the game here either;
- that the captured **entities** appear in a real client and are alive there: the blob
  format and the parsing are proven offline, but no live client has loaded them yet;
- that the **per-chunk entity rule drops only entities that are really gone**. The rule
  rests on two facts about the client — the refresh pass rewrites every loaded entity, and
  a chunk brings its entities in — and on the capture's own ordering (a chunk receipt
  followed by entity records). All of that is measured on a real capture and covered by
  unit tests, but "the mob standing at those coordinates on the server was already dead"
  is a statement about the server that no client capture can confirm. What is checked is
  the weaker, safe direction: an entity is never dropped on a session's silence alone, and
  a chunk that was not received again keeps everything;
- that a built world **opens at the captured time of day and season** and **starts the
  player at the captured position**: the fields are proven to be in `gamedata` by the
  offline selftest, but the client that loads the world has not been run here. The
  season in particular depends on the game deriving it from the restored clock and the
  world configuration, which is decompiled behaviour, not observed;
- whether the restored world **age** is what temporal storms should see (it is carried
  over deliberately, but the storm schedule was not observed either);
- how much the entity refresh adds to the capture file on a busy server;
- behaviour under load: fast travel, FPS drops, queue overflow;
- the "local registry" path — on the test machine the `localregistry.json` snapshot
  never appeared, so every build here went through the server numbering. The translated
  path is covered by the offline selftest (including the table it now writes), but a
  world built with a live snapshot has not been opened in the game;
- several servers in one game session (the capture directory is currently shared).

## After a game update

`VsFullCapture` depends on the closed-source `VintagestoryLib`. Run
`./probe-vcdbs.sh` — it is the regression test.
