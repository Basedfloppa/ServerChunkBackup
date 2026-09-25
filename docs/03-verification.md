# What is verified, and what is not

Everything below was checked against **Vintage Story 1.22.7**. Statements that come
from decompiling the closed-source assembly rather than from public documentation may
change in the next game version.

## By the tests in this repository

`dotnet test VsChunkDump.slnx` — **133 tests**:

- the `vschunkdump` dump format, palette + bit planes, CRC32, deduplication, resume;
- PNG rendering (top view, isometric, slice) and the `demo` generator;
- the `vscapture` capture format: round-trip, Brotli compression, CRC, truncation
  resilience, block entities;
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
capture → `.vcdbs` → comparison of blocks, translated ids and block entities.

> `modzip` checks **the archive structure and `modinfo.json` parsing** only. It does
> not load the assembly at runtime, so a crash in `ModSystem` or an incompatibility
> with the game version cannot be caught this way.

## In the game

The mod ran in a real 1.22.7 client: the Harmony patch on
`SystemNetworkProcess.ProcessInBackground` installed, auto-build assembled a world
from a real capture (122,119 records, 7,752 chunks, 646 columns), and `verify` of the
result passed — 200/200 chunks, 99.5 % of blocks non-empty.

## Still needs checking

- that a built world **opens and looks correct** — only the structural `verify` passes;
- behaviour under load: fast travel, FPS drops, queue overflow;
- the "local registry" path — on the test machine the `localregistry.json` snapshot
  never appeared and the build went through `BlockIDs`;
- several servers in one game session (the capture directory is currently shared).

## After a game update

`VsFullCapture` depends on the closed-source `VintagestoryLib`. Run
`./probe-vcdbs.sh` — it is the regression test.
