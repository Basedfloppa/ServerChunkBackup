using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Common.Database;
using Vintagestory.Server;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Silent logger: LoggerBase only requires LogImpl to be implemented.
/// Our own, not the game's Vintagestory.Common.NullLogger — that one writes log files.
/// </summary>
public sealed class SilentLogger : LoggerBase
{
    protected override void LogImpl(EnumLogType logType, string format, params object[] args) { }
}

public sealed class BuildOptions
{
    /// <summary>
    /// Template world: gamedata, mapregion and the rest are taken from it. Optional —
    /// if empty, the save is created from scratch (<see cref="TemplateFactory"/>).
    /// </summary>
    public string TemplatePath { get; set; } = "";

    /// <summary>Name of the new world (only for a template-less build).</summary>
    public string WorldName { get; set; } = "";

    public string OutputPath { get; set; } = "";

    /// <summary>
    /// Allow overwriting an existing world file. Disabled by default: the builder
    /// deletes the target file before writing, so overwriting must be deliberate
    /// (`--force` in the CLI, `OverwriteBuiltWorld` in the mod settings).
    /// </summary>
    public bool Force { get; set; }

    public int Dim { get; set; }

    /// <summary>
    /// An optional extra bound on top of the chunk rule: leave out entities that no record
    /// has mentioned for more than this many connections (game sessions) — see
    /// <see cref="CaptureModel.EntitiesByAge"/>. Non-positive (the default) applies no such
    /// bound, and entity freshness is decided by the chunk rule alone.
    ///
    /// The chunk rule is the one that matters and is always applied: an entity is left out
    /// when the chunk it stands in was received again in a later connection that recorded
    /// entities, and the entity was not among them. A capture is appended to across game
    /// runs, so it holds what the client saw over many sessions rather than the state of
    /// the server, and a mob that died out of view never produces a despawn record — but a
    /// place the client never returned to says nothing about its mobs, so nothing there is
    /// erased. See <see cref="CaptureModel.SupersedeConnections"/>.
    ///
    /// This bound exists for the leftovers of the other case: a capture whose newest
    /// connections never covered some chunk, so the rule has nothing to compare against.
    /// A positive value cuts by age instead. The build log always reports how many were
    /// left out and why.
    /// </summary>
    public int EntityMaxAgeConnections { get; set; } = -1;

    /// <summary>Write mapchunk rows. Without them the game silently ignores our chunks.</summary>
    public bool WriteMapChunks { get; set; } = true;

    /// <summary>
    /// Table "server id → id in this world". If set, block, liquid and decor ids are
    /// translated directly in the chunk data — by rewriting the blob palette.
    ///
    /// This is the right way: blobs store server ids, while the client has its own
    /// numbering (ours already diverged at id 2). Asking the game to remap blocks
    /// via ServerSystemBlockIdRemapper is unreliable — it moves live blocks and
    /// conflicts with those not in the table. Translation in the data does not depend on it.
    ///
    /// With translation the world still needs a BlockIDs table — the TARGET registry
    /// ("id in this world → code"), not the server's. See <see cref="WriteBlockIds"/>.
    /// </summary>
    public Dictionary<int, int>? BlockIdMap { get; set; }

    /// <summary>
    /// Local block registry ("id in this world → code"). If it is set but
    /// <see cref="BlockIdMap"/> is not, the translation is built automatically by
    /// block code: the code is the only thing that is the same on the server and in
    /// this world.
    ///
    /// The registry is taken with the command <c>vsfullcapture-writer registry &lt;world.vcdbs&gt;</c>
    /// or from the snapshot the mod takes.
    /// </summary>
    public BlockRegistryTable? LocalRegistry { get; set; }

    /// <summary>
    /// Block code used to replace blocks missing from this world. Empty — replacement
    /// with air (id 0). The code is looked up in <see cref="LocalRegistry"/>.
    /// </summary>
    public string? MissingBlockCode { get; set; }

    /// <summary>
    /// Write the id → code table into gamedata.ModData["BlockIDs"]. The game's
    /// ServerSystemBlockIdRemapper reads it on AssetsFirstLoaded, learns from it what
    /// the ids in the chunks mean, and moves its live blocks to those ids. The table is
    /// therefore ALWAYS needed, and it must describe the numbering of the chunks:
    ///
    ///   • ids translated — the table is <see cref="LocalRegistry"/>, the registry the
    ///     translation was made into;
    ///   • ids not translated — the table is the capture's server registry.
    ///
    /// With an empty table the remapper has nothing to learn from and keeps its own
    /// numbering: every chunk is then read with the blocks shifted, and a block entity
    /// whose class reads its block (BlockEntityCage, for one) throws on the client.
    /// </summary>
    public bool WriteBlockIds { get; set; } = true;

    public int WorldGenVersion { get; set; } = 3;
}

public sealed class BuildReport
{
    public int ColumnsCaptured;
    public int ColumnsWritten;
    public int ColumnsSkippedNoMapChunk;
    public int ColumnsSkippedIncomplete;
    public int ChunksWritten;
    public int MapChunksWritten;
    public int BlockRegistrySize;
    public bool BlockIdsWritten;
    public bool RequireRemapping;

    /// <summary>Id translation is enabled: blob palettes are rewritten to local ids.</summary>
    public bool BlockIdsTranslated;

    /// <summary>A string like "id translation: N entries (in place X, shifted Y, missing locally Z)".</summary>
    public string? IdTranslation;

    /// <summary>
    /// Block entities whose declared block is missing from the target registry: the block
    /// itself was replaced with the fallback (air by default), so the entity is left
    /// without a block. Such entries are not written — the game deletes them on load, and
    /// a class that reads its block (BlockEntityCage) throws instead.
    /// </summary>
    public int BlockEntitiesStaleBlock;

    /// <summary>
    /// Block entities whose block at their position is a different block (an entity left
    /// over from before its block was removed or replaced). Not written: the game deletes
    /// them on load with a warning, and a class that reads its block throws on the client.
    /// </summary>
    public int BlockEntitiesWrongBlock;

    /// <summary>Block entities without a "blockCode" — there is nothing to compare them against.</summary>
    public int BlockEntitiesNoBlockCode;

    /// <summary>How many palette entries were rewritten (total across block and liquid layers).</summary>
    public long PaletteEntries;
    public long PaletteChanged;
    public long PaletteFallback;
    public int PaletteCompressedLayers;
    public int PaletteRawLayers;

    /// <summary>Layers that could not be translated (the blob still has server ids).</summary>
    public int PaletteSkippedLayers;

    /// <summary>How many block entities were written to the world (without them chiseled blocks are empty).</summary>
    public long BlockEntities;

    /// <summary>How many block entities had to be rewritten (id translation inside the data).</summary>
    public long BlockEntitiesRewritten;

    /// <summary>How many block entities failed to parse (the data was left as is).</summary>
    public long BlockEntitiesUnparsed;

    /// <summary>Chiseled-block materials translated to codes.</summary>
    public long MaterialsAsCodes;

    /// <summary>Materials not found in the server registry.</summary>
    public long MaterialsUnknown;

    /// <summary>Decor entries inside block entities translated by the id map.</summary>
    public long DecorIdsInBlockEntities;

    /// <summary>Block entities that failed to parse and were skipped.</summary>
    public long BlockEntitiesDropped;

    /// <summary>How many entities were written into chunks (mobs, dropped items, item frames).</summary>
    public long Entities;

    /// <summary>How many entities the capture holds in total.</summary>
    public long EntitiesInCapture;

    /// <summary>
    /// Entities of the capture that were not written: their chunk is not in the
    /// assembled world (an incomplete column or a column without a heightmap).
    /// </summary>
    public long EntitiesNotWritten;

    /// <summary>Entities whose data was empty or had no class name and were skipped.</summary>
    public long EntitiesDropped;

    /// <summary>
    /// Entities left out because their chunk was received again later, in a connection that
    /// recorded entities, and they were not among them — stale knowledge the capture itself
    /// superseded (<see cref="CaptureModel.SupersedeConnections"/>).
    /// </summary>
    public long EntitiesSuperseded;

    /// <summary>Entities left out by the optional age bound (<see cref="BuildOptions.EntityMaxAgeConnections"/>, positive only).</summary>
    public long EntitiesTooOld;

    /// <summary>The age bound the build applied: the option's value when it is positive, otherwise -1 (none).</summary>
    public int EntityMaxAgeUsed = -1;

    /// <summary>
    /// Chunk sections whose last receipt came from a connection that recorded no entity, so
    /// the rule had no newer entity information about them and kept their entities as they
    /// were — see <see cref="CaptureModel.ChunksWithoutNewerEntityInfo"/>.
    /// </summary>
    public int ChunksWithoutNewerEntityInfo;

    /// <summary>Chunks that arrived with a compression version different from the one the builder writes.</summary>
    public int ChunkCompressionMismatch;

    /// <summary>How many server connections are reflected in the capture.</summary>
    public int Connections;

    public readonly List<string> Warnings = [];
}

/// <summary>
/// Assembles a real .vcdbs from the capture of server data.
///
/// Key rules (verified against the 1.22.7 decompile of ServerSystemSupplyChunks):
///   • the mapchunk row is MANDATORY, otherwise chunk rows are not read;
///   • a column must be written ENTIRELY (all Y sections), otherwise it is discarded;
///   • in mapchunk, currentpass = Done and WorldGenVersion >= 3 are required, and
///     RainHeightMap must not be null.
/// </summary>
public static class WorldBuilder
{
    /// <summary>Key in gamedata.ModData where ServerSystemBlockIdRemapper keeps the id → code table.</summary>
    private const string BlockIdsKey = "BlockIDs";

    public static BuildReport Build(CaptureModel model, BuildOptions options, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(options.OutputPath))
            throw new InvalidOperationException("Output path for the assembled world is not set (-o/--out).");

        bool useTemplate = !string.IsNullOrEmpty(options.TemplatePath);
        if (useTemplate)
        {
            if (!File.Exists(options.TemplatePath))
                throw new FileNotFoundException("Template world not found: " + options.TemplatePath);

            if (string.Equals(Path.GetFullPath(options.TemplatePath), Path.GetFullPath(options.OutputPath),
                    StringComparison.Ordinal))
                throw new InvalidOperationException("The output file must not be the same as the template.");
        }

        // --- refusals BEFORE any deletions: the target world must not be damaged ---
        if (model.Identification == null)
        {
            throw new InvalidOperationException(
                "No server identification packet in the capture, so the world dimensions are unknown "
                + "(MapSizeX/Z). Such a save would not open anyway — take a new capture with a mod "
                + "that writes the world parameters. The existing file is untouched.");
        }
        if (model.MapSizeX <= 0 || model.MapSizeZ <= 0)
        {
            throw new InvalidOperationException(
                $"Invalid world dimensions in the capture (MapSize {model.MapSizeX}x{model.MapSizeY}x{model.MapSizeZ}) — "
                + "there is nothing to build. The existing file is untouched.");
        }
        if (model.Chunks.Count == 0)
            throw new InvalidOperationException("No chunks in the capture — there is nothing to build.");

        var readiness = model.ColumnReadiness();
        if (readiness.Complete == 0)
        {
            throw new InvalidOperationException(
                $"No complete column with a heightmap in the capture (complete {readiness.Complete}, "
                + $"without heightmap {readiness.NoMapChunk}, incomplete {readiness.Incomplete}) — nothing to build. "
                + "The existing world file is untouched.");
        }
        if (File.Exists(options.OutputPath) && !options.Force)
        {
            throw new InvalidOperationException(
                $"File \"{options.OutputPath}\" already exists and overwriting is not allowed. "
                + "It erases the world irrevocably: use --force (CLI) or OverwriteBuiltWorld in the mod settings.");
        }

        // Build into a TEMPORARY file: until the new world is ready, the old one is
        // untouched. Otherwise a crash/shutdown in the middle would leave the user with neither.
        string outputPath = options.OutputPath;
        string workPath = outputPath + ".building";
        DeleteWorldFiles(workPath);
        try
        {
            BuildReport report = BuildInto(model, options, workPath, log);
            DeleteWorldFiles(outputPath);
            File.Move(workPath, outputPath, overwrite: true);
            log($"World written: {outputPath}");
            return report;
        }
        finally
        {
            // If the build failed — do not leave the temporary file. If the move
            // already happened, DeleteWorldFiles simply finds nothing.
            try { DeleteWorldFiles(workPath); } catch (Exception) { /* already moved */ }
        }
    }

    private static BuildReport BuildInto(CaptureModel model, BuildOptions options, string outputPath, Action<string> log)
    {
        var report = new BuildReport
        {
            RequireRemapping = model.Identification?.RequireRemapping > 0
        };

        bool useTemplate = !string.IsNullOrEmpty(options.TemplatePath);

        int sectionsPerColumn = model.SectionsPerColumn;
        log($"Template: {options.TemplatePath}");
        if (model.SectionsInferred)
        {
            log($"Sections per column: {sectionsPerColumn} (inferred from the capture: "
                + "the server identification packet was not caught, so the world height is unknown)");
            report.Warnings.Add(
                $"World height unknown (no identification packet) — assuming {sectionsPerColumn} sections along Y "
                + "by the highest section in the capture. If this is wrong, columns will be skipped.");
        }
        else
        {
            log($"Sections per column (MapSizeY={model.MapSizeY}): {sectionsPerColumn}");
        }

        ResolveBlockIdMap(model, options, report, log);
        BlockRegistryTable? blockIdsTable = ResolveBlockIdsTable(model, options, report, log);

        if (useTemplate)
        {
            File.Copy(options.TemplatePath, outputPath, overwrite: true);
            log($"Template copied to: {outputPath}");
        }
        else
        {
            string worldName = string.IsNullOrWhiteSpace(options.WorldName)
                ? "Captured world"
                : options.WorldName;
            TemplateFactory.CreateNew(outputPath, model, worldName, blockIdsTable, log);
            log($"World created from scratch: {outputPath}");

            // The block registry has already been written into this save by TemplateFactory.
            report.BlockRegistrySize = blockIdsTable?.Count ?? 0;
            report.BlockIdsWritten = report.BlockRegistrySize > 0;
        }

        var pool = new StandaloneChunkDataPool();
        // The codec is needed both for translation and for the "which block is under this
        // entity" check (the bit planes are always a zstd frame), so it is unconditional.
        var codec = new GameZstdCodec();
        var serverRegistry = model.BlockRegistry();

        // Group the captured chunks by column.
        var byColumn = new Dictionary<(int X, int Z), List<ChunkPayload>>();
        foreach (var chunk in model.Chunks.Values)
        {
            if (!byColumn.TryGetValue((chunk.X, chunk.Z), out var list))
            {
                list = [];
                byColumn[(chunk.X, chunk.Z)] = list;
            }
            list.Add(chunk);
        }
        report.ColumnsCaptured = byColumn.Count;

        // Entities of the capture, grouped by the chunk section their position falls into.
        //
        // The entity set is narrowed by the chunk rule, not by a global cut-off: an entity
        // is left out only when the chunk it stands in was received again in a later
        // connection that recorded entities and the entity was not among them. A chunk that
        // was never received again keeps its entities — no information about it is not
        // information that it is empty. See CaptureModel.SupersedeConnections.
        int entityMaxAge = options.EntityMaxAgeConnections > 0 ? options.EntityMaxAgeConnections : -1;
        report.EntityMaxAgeUsed = entityMaxAge;
        report.ChunksWithoutNewerEntityInfo = model.ChunksWithoutNewerEntityInfo;

        EntityScope entityScope = default;
        var entitiesByChunk = model.Entities.Count > 0
            ? model.GroupEntitiesByChunk(entityMaxAge, out entityScope)
            : new Dictionary<(int X, int Y, int Z), List<EntityPayload>>();
        report.EntitiesSuperseded = entityScope.Superseded;
        report.EntitiesTooOld = entityScope.TooOld;

        var chunkRows = new List<DbChunk>(model.Chunks.Count);
        var mapChunkRows = new List<DbChunk>(byColumn.Count);

        foreach (var ((cx, cz), chunks) in byColumn.OrderBy(k => k.Key.Z).ThenBy(k => k.Key.X))
        {
            // Rule 1: the column must be complete.
            var sections = chunks.Select(c => c.Y).Distinct().OrderBy(y => y).ToList();
            bool complete = sections.Count == sectionsPerColumn
                            && sections[0] == 0
                            && sections[^1] == sectionsPerColumn - 1;
            if (!complete)
            {
                report.ColumnsSkippedIncomplete++;
                continue;
            }

            // Rule 2: without a mapchunk row the game will not read these chunks.
            if (!model.MapChunks.TryGetValue((cx, cz), out var mapChunk))
            {
                report.ColumnsSkippedNoMapChunk++;
                continue;
            }

            foreach (var chunk in chunks)
            {
                IReadOnlyList<EntityPayload>? chunkEntities = null;
                if (entitiesByChunk.TryGetValue((chunk.X, chunk.Y, chunk.Z), out var list))
                {
                    chunkEntities = list;
                }

                chunkRows.Add(new DbChunk(
                    new ChunkPos(chunk.X, chunk.Y, chunk.Z, options.Dim),
                    BuildChunkBlob(chunk, pool, options.BlockIdMap, codec, report, serverRegistry, model,
                        chunkEntities, options.LocalRegistry)));
                report.ChunksWritten++;

                // The pool accumulates freed layer arrays and does not hand them out
                // (Request always creates a new ChunkData). On large builds this is
                // hundreds of megabytes, so we clean up periodically.
                if ((report.ChunksWritten & 0xFF) == 0) pool.FreeAll();
            }

            if (options.WriteMapChunks)
            {
                mapChunkRows.Add(new DbChunk(
                    new ChunkPos(cx, 0, cz, options.Dim),
                    BuildMapChunkBlob(mapChunk, options)));
                report.MapChunksWritten++;
            }

            report.ColumnsWritten++;
        }

        if (report.ChunksWritten == 0)
            report.Warnings.Add("No complete column with a heightmap found — there is nothing to write.");

        if (report.ColumnsSkippedNoMapChunk > 0)
        {
            report.Warnings.Add(
                $"{report.ColumnsSkippedNoMapChunk} columns skipped: no mapchunk row. "
                + "Without it the game ignores the chunks, so these columns were not written. "
                + "Check that CaptureMapChunks is enabled in the mod.");
        }

        if (report.ColumnsSkippedIncomplete > 0)
        {
            report.Warnings.Add(
                $"{report.ColumnsSkippedIncomplete} columns skipped: the column was not captured in full. "
                + "Walk around in the game longer so the client receives all Y sections.");
        }

        report.Connections = model.Connections;
        if (model.Connections > 1)
        {
            report.Warnings.Add(
                $"The capture has {model.Connections} server connections (the directory is appended to between sessions). "
                + "The block registry and world configuration were taken from the LAST connection — if the servers or "
                + "mod sets differed, blocks from earlier sessions may be translated incorrectly. "
                + "For a clean result, capture into a separate directory (CaptureName).");
        }

        if (report.ChunkCompressionMismatch > 0)
        {
            report.Warnings.Add(
                $"{report.ChunkCompressionMismatch} chunks arrived with a compression version different from "
                + $"{ChunkBlobWriter.CompressionVersion} — blocks in them may not read. "
                + "Check that the capture's game version matches the installed one.");
        }

        if (report.BlockEntitiesUnparsed > 0)
        {
            report.Warnings.Add(
                $"{report.BlockEntitiesUnparsed} block entities failed to parse and were skipped — "
                + "the corresponding chiseled blocks, chests and machines will stay empty in the world.");
        }

        int notOnTheirBlock = report.BlockEntitiesWrongBlock + report.BlockEntitiesStaleBlock;
        if (notOnTheirBlock > 0)
        {
            report.Warnings.Add(
                $"{notOnTheirBlock} block entities were left over from before their block changed "
                + "(the capture kept them after the block was removed or replaced) and were not written — "
                + "the game deletes such entities on load, and a class that reads its block would throw on the client.");
        }

        // Entities are written into their own chunk: if a column was skipped, its
        // entities have nowhere to go.
        report.EntitiesInCapture = model.Entities.Count;
        report.EntitiesNotWritten = report.EntitiesInCapture - report.Entities - report.EntitiesDropped
                                    - report.EntitiesSuperseded - report.EntitiesTooOld;
        if (report.EntitiesNotWritten > 0)
        {
            report.Warnings.Add(
                $"{report.EntitiesNotWritten} of {report.EntitiesInCapture} entities were not written: "
                + "their chunk is not in the world (the column was not captured in full or has no heightmap). "
                + "Mobs from those columns will be missing — walk around those places in the game so the "
                + "client receives the whole column.");
        }

        if (report.EntitiesSuperseded > 0)
        {
            report.Warnings.Add(
                $"{report.EntitiesSuperseded} of {report.EntitiesInCapture} entities were left out: their chunk was "
                + "received again later, in a connection that recorded entities, and they were not among them — the "
                + "capture superseded its own knowledge of them. Places the client never returned to kept their entities.");
        }

        if (report.EntitiesTooOld > 0)
        {
            report.Warnings.Add(
                $"{report.EntitiesTooOld} of {report.EntitiesInCapture} entities were left out by the age bound: "
                + $"no record mentioned them for more than {report.EntityMaxAgeUsed} connection(s) "
                + "(EntityMaxAgeConnections). An entity the client stopped watching may be alive on the server — "
                + "this bound is applied on top of the chunk rule, not instead of it.");
        }

        // --- write to the database ---
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;

        string? error = null;
        if (!connection.OpenOrCreate(outputPath, ref error, requireWriteAccess: true,
                corruptionProtection: false, doIntegrityCheck: false))
        {
            throw new InvalidOperationException(
                "Failed to open the world for writing" + (error != null ? ": " + error : "")
                + ". Make sure the game is closed and the world is not open.");
        }

        // The template's world height must match the server's, otherwise the game
        // simply will not see sections above its own height. For a from-scratch build
        // there is nothing to check: the world configuration comes from the capture.
        if (!useTemplate) goto afterTemplateChecks;
        try
        {
            byte[] templateGameData = db.GetGameData();
            if (templateGameData is { Length: > 0 })
            {
                SaveGame templateSave = SerializerUtil.Deserialize<SaveGame>(templateGameData);
                int templateSections = Math.Max(1, templateSave.MapSizeY / 32);
                if (templateSections < sectionsPerColumn)
                {
                    report.Warnings.Add(
                        $"World height in the template is {templateSave.MapSizeY} blocks ({templateSections} sections), "
                        + $"while the capture has {sectionsPerColumn} sections. The game will not see sections above {templateSections - 1}: "
                        + "create a template world with the same height as on the server.");
                    log($"WARNING: the template is lower than the capture ({templateSave.MapSizeY} < {sectionsPerColumn * 32} blocks)");
                }
                else if (templateSave.Seed != model.Identification?.Seed && model.Identification != null)
                {
                    report.Warnings.Add(
                        $"Template seed ({templateSave.Seed}) does not match the server seed "
                        + $"({model.Identification.Seed}) — the terrain under the builds will be different.");
                }
            }
        }
        catch (Exception e)
        {
            report.Warnings.Add("Failed to verify template parameters: " + e.Message);
        }

        afterTemplateChecks:

        // For a template-less build BlockIDs has already been written into the new save.
        if (useTemplate && blockIdsTable is { Count: > 0 })
        {
            WriteBlockIds(db, blockIdsTable.Blocks);
            report.BlockRegistrySize = blockIdsTable.Count;
            report.BlockIdsWritten = true;
            log($"BlockIDs: wrote {blockIdsTable.Count} id → code mappings into the template");
        }

        // In a template build gamedata comes from the template, so everything the capture
        // says about the save itself is written into it here (a from-scratch build does all
        // of that in TemplateFactory).
        if (useTemplate) UpdateTemplateGameData(db, model, log);

        pool.FreeAll();

        if (chunkRows.Count > 0) db.SetChunks(chunkRows);
        if (mapChunkRows.Count > 0) db.SetMapChunks(mapChunkRows);

        log($"Chunks written: {report.ChunksWritten}, heightmaps: {report.MapChunksWritten}");
        log($"Entities written: {report.Entities} of {report.EntitiesInCapture}"
            + EntityAgeSummary(model, report));

        // --- read-back check with the same game class ---
        var positions = chunkRows.Take(50).Select(r => r.Position).ToList();
        int readBack = db.GetChunks(positions).Count();
        log($"Read-back check: {readBack} of {positions.Count} control rows found");

        return report;
    }

    /// <summary>
    /// What the build did with the entity set, for the build log: how old the knowledge is
    /// and what the chunk rule left out. A despawn is only captured for an entity the client
    /// was tracking, so without this line a world full of mobs that died on the server looks
    /// exactly as good as a freshly walked one.
    /// </summary>
    private static string EntityAgeSummary(CaptureModel model, BuildReport report)
    {
        if (model.Entities.Count == 0) return "";

        var byAge = model.EntitiesByAge();
        if (byAge.Count == 0) return "";

        int fresh = byAge.GetValueOrDefault(0);
        int recent = 0;
        foreach (var (age, count) in byAge)
        {
            if (age is > 0 and <= 3) recent += count;
        }
        int older = model.Entities.Count - fresh - recent;

        string text = $"; last seen: {fresh} in the last connection, {recent} within three, {older} earlier";
        if (report.EntitiesSuperseded > 0) text += $"; {report.EntitiesSuperseded} superseded by a later look at their chunk";
        if (report.EntitiesTooOld > 0) text += $"; {report.EntitiesTooOld} older than {report.EntityMaxAgeUsed} connection(s)";
        if (report.ChunksWithoutNewerEntityInfo > 0)
            text += $"; {report.ChunksWithoutNewerEntityInfo} chunk(s) never revisited with entity data, kept as they were";
        return text;
    }

    /// <summary>Delete the world file and its SQLite journals (-wal, -shm, -journal).</summary>
    public static void DeleteWorldFiles(string worldPath)
    {
        foreach (string path in new[]
                 {
                     worldPath,
                     worldPath + "-wal",
                     worldPath + "-shm",
                     worldPath + "-journal"
                 })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // The file may be busy — then it is better to fail with an explicit
                // error than to silently corrupt the database.
                throw new IOException("Failed to delete \"" + path + "\". Close the game and try again.");
            }
        }
    }

    /// <summary>
    /// A chunk table row. The block/light/liquid blobs from the packet are placed
    /// VERBATIM: the wire packet and save layouts match byte for byte, so no
    /// re-encoding is needed. The exception is block and liquid palettes: they hold
    /// server ids, so they are rewritten when translation is enabled.
    ///
    /// We do not touch the light layer: its palette holds packed light values, not
    /// block ids, so there is nothing to translate there.
    /// </summary>
    private static byte[] BuildChunkBlob(ChunkPayload packet, StandaloneChunkDataPool pool,
        Dictionary<int, int>? idMap, IZstdCodec? codec, BuildReport report,
        IReadOnlyDictionary<int, string>? serverRegistry, CaptureModel model,
        IReadOnlyList<EntityPayload>? entities, BlockRegistryTable? targetRegistry)
    {
        _ = pool; // the pool is no longer needed: the blob is assembled manually

        // Blob compression version: ours is always written to the save, so a
        // mismatch should at least be shown.
        if (packet.Compver != 0 && packet.Compver != ChunkBlobWriter.CompressionVersion)
            report.ChunkCompressionMismatch++;

        // The server sends empty chunks with null instead of blobs — substitute only
        // what actually arrived.
        byte[]? blocks = packet.Blocks != null
            ? TranslateLayer(packet.Blocks, idMap, codec, report, "blocks")
            : null;
        byte[]? liquids = packet.Liquids != null
            ? TranslateLayer(packet.Liquids, idMap, codec, report, "liquids")
            : null;

        // Block entities: without them chiseled blocks, chests, machines and the rest
        // remain empty blocks.
        var entries = BuildBlockEntityEntries(
            model.BlockEntitiesFor(packet), packet.Blocks, codec, serverRegistry, idMap, targetRegistry, report);

        // Entities: mobs, dropped items, item frames sitting in this section.
        var entityEntries = BuildEntityEntries(entities, report);

        var moddata = ReadModdata(packet);

        return ChunkBlobWriter.Write(
            blocks,
            packet.Light,
            packet.LightSat,
            liquids,
            entityEntries,
            entries,
            moddata,
            packet.LightPositions.Length > 0 ? packet.LightPositions : null,
            BuildDecors(packet, idMap),
            GameVersion.ShortGameVersion,
            packet.Empty);
    }

    /// <summary>
    /// Prepare entity entries for the chunk row. Entities reach the capture already in
    /// savegame form (<see cref="EntitySaveData"/>), so unlike block entities there is
    /// nothing to translate inside them — only to pack "class name + bytes".
    /// </summary>
    private static List<byte[]> BuildEntityEntries(IReadOnlyList<EntityPayload>? entities, BuildReport report)
    {
        if (entities is not { Count: > 0 }) return [];

        var entries = new List<byte[]>(entities.Count);
        foreach (var entity in entities)
        {
            if (entity.SaveData.Length == 0 || string.IsNullOrEmpty(entity.Classname))
            {
                report.EntitiesDropped++;
                continue;
            }
            entries.Add(ChunkBlobWriter.ToSaveEntry(entity.Classname, entity.SaveData));
            report.Entities++;
        }
        return entries;
    }

    /// <summary>
    /// Prepare block entity entries for the chunk row: translate ids inside the data
    /// (chiseled-block materials, decor) and pack them into the save format.
    /// </summary>
    private static List<byte[]> BuildBlockEntityEntries(
        List<BlockEntityPayload> blockEntities,
        byte[]? blocksBlob,
        IZstdCodec? codec,
        IReadOnlyDictionary<int, string>? serverRegistry,
        Dictionary<int, int>? idMap,
        BlockRegistryTable? targetRegistry,
        BuildReport report)
    {
        // With translation the block at a position is the target registry's block — or the
        // fallback, when the server's code is missing locally. An entity whose own block
        // was not written has nothing under it, so it must not be written either.
        HashSet<string>? targetCodes = null;
        if (targetRegistry is { Count: > 0 })
        {
            targetCodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (string code in targetRegistry.Blocks.Values)
                targetCodes.Add(BlockRegistryTable.NormalizeCode(code));
        }

        var entries = new List<byte[]>(blockEntities.Count);
        foreach (var be in blockEntities)
        {
            byte[] data = BlockEntityRewrite.Rewrite(be, serverRegistry, idMap, out var stats);
            report.BlockEntities++;

            // The entity must sit on its own block. The game resolves the block on the
            // client by position alone (ClientSystemEntities.UpdateBlockEntityData) and
            // puts it into the entity; if it is not the block the entity declares, the
            // entity is stale — a leftover from before its block was removed or replaced.
            if (blocksBlob != null && stats is { HasPosition: true, BlockCode: { Length: > 0 } declared }
                && serverRegistry is { Count: > 0 })
            {
                int px = stats.PosX, py = stats.PosY, pz = stats.PosZ;
                int lx = px % 32, ly = py % 32, lz = pz % 32;
                if (lx >= 0 && ly >= 0 && lz >= 0)
                {
                    int index = (ly * 32 + lz) * 32 + lx;
                    if (CombinedLayerBlob.TryReadBlockId(blocksBlob, codec, index, out int underId)
                        && serverRegistry.TryGetValue(underId, out string? underCode)
                        && !string.IsNullOrEmpty(underCode)
                        && BlockRegistryTable.NormalizeCode(underCode) != BlockRegistryTable.NormalizeCode(declared))
                    {
                        report.BlockEntitiesWrongBlock++;
                        report.BlockEntitiesDropped++;
                        if (report.BlockEntitiesWrongBlock <= 3)
                        {
                            report.Warnings.Add(
                                $"Block entity \"{be.Classname}\" at {px}, {py}, {pz} declares block "
                                + $"\"{declared}\", but the world has \"{underCode}\" there — the entry is left "
                                + "over from before the block changed and is not written (the game deletes such "
                                + "entities, and a class that reads its block would throw on the client).");
                        }
                        continue;
                    }
                }
            }

            if (targetCodes != null)
            {
                if (string.IsNullOrEmpty(stats.BlockCode))
                {
                    report.BlockEntitiesNoBlockCode++;
                }
                else if (!targetCodes.Contains(BlockRegistryTable.NormalizeCode(stats.BlockCode)))
                {
                    report.BlockEntitiesStaleBlock++;
                    report.BlockEntitiesDropped++;
                    if (report.BlockEntitiesStaleBlock <= 3)
                    {
                        report.Warnings.Add(
                            $"Block entity \"{be.Classname}\" at {be.X}, {be.Y}, {be.Z} declares block "
                            + $"\"{stats.BlockCode}\", which this world does not have — the block was replaced "
                            + "with the fallback, so the entity is not written (the game would delete it, and "
                            + "a class that reads its block would throw on the client).");
                    }
                    continue;
                }
            }
            if (stats.Rewritten > 0) report.BlockEntitiesRewritten++;
            report.MaterialsAsCodes += stats.MaterialsAsCodes;
            report.MaterialsUnknown += stats.MaterialsUnknown;
            report.DecorIdsInBlockEntities += stats.DecorIdsTranslated;
            if (stats.Failed > 0)
            {
                // Writing data that failed to parse into the world is dangerous (the
                // game may not read the chunk), so we skip the entry — but count it.
                report.BlockEntitiesUnparsed++;
                report.BlockEntitiesDropped++;
                if (report.BlockEntitiesUnparsed <= 3)
                {
                    report.Warnings.Add(
                        $"Block entity data \"{be.Classname}\" failed to parse — entry skipped.");
                }
                continue;
            }

            entries.Add(ChunkBlobWriter.ToSaveEntry(be.Classname, data));
        }
        return entries;
    }

    private static Dictionary<string, byte[]>? ReadModdata(ChunkPayload packet)
    {
        if (packet.Moddata is not { Length: > 0 }) return null;
        try
        {
            return SerializerUtil.Deserialize<Dictionary<string, byte[]>>(packet.Moddata);
        }
        catch (Exception)
        {
            // Moddata is not critical for displaying builds — we simply lose it.
            return null;
        }
    }

    /// <summary>
    /// Rewrite the palette of a layer to local ids. If there is nothing to translate
    /// or the blob failed to parse, we return the original bytes as is and count it
    /// in the report.
    /// </summary>
    private static byte[] TranslateLayer(byte[] blob, Dictionary<int, int>? idMap,
        IZstdCodec? codec, BuildReport report, string layerName)
    {
        if (idMap is not { Count: > 0 }) return blob;

        if (CombinedLayerBlob.TryTranslatePalette(blob, idMap, 0, codec, out byte[] translated, out var stats))
        {
            report.PaletteEntries += stats.Entries;
            report.PaletteChanged += stats.Changed;
            report.PaletteFallback += stats.ReplacedByFallback;
            if (stats.WasCompressed) report.PaletteCompressedLayers++;
            else report.PaletteRawLayers++;
            return translated;
        }

        // An empty palette and "all ids are already local" are normal, but an
        // unparsed non-empty blob means the layer will keep server ids. That needs
        // to be visible in the log.
        if (CombinedLayerBlob.ReadLengthCode(blob) != 0
            && !CombinedLayerBlob.TryReadPalette(blob, codec, out _, out _, out _))
        {
            report.PaletteSkippedLayers++;
            if (report.PaletteSkippedLayers <= 3)
            {
                report.Warnings.Add(
                    $"Layer \"{layerName}\" failed to parse (length {blob.Length} bytes) — its blocks will keep "
                    + "server ids. Check that the game version and mod set match.");
            }
        }

        return blob;
    }

    /// <summary>
    /// Determine the id translation before writing: either it is set directly, or
    /// built from the local registry, or (if there is no local registry) the old
    /// way through BlockIDs remains.
    /// </summary>
    private static void ResolveBlockIdMap(CaptureModel model, BuildOptions options, BuildReport report,
        Action<string> log)
    {
        if (options.BlockIdMap is { Count: > 0 })
        {
            report.BlockIdsTranslated = true;
            log($"block ids are translated in the data: {options.BlockIdMap.Count} entries");
        }
        else if (options.LocalRegistry is { Count: > 0 })
        {
            var captured = model.BlockRegistry();
            if (captured.Count == 0)
            {
                report.Warnings.Add("No block registry (ServerAssets) in the capture — id translation is impossible.");
            }
            else
            {
                int fallbackId = ResolveMissingBlockId(options, report, log);
                var built = BlockIdMapBuilder.Build(captured, options.LocalRegistry.Blocks, fallbackId);
                report.IdTranslation = built.Describe();
                log(built.Describe());
                if (built.MissingSamples.Count > 0)
                {
                    log("missing locally, for example: " + string.Join(", ", built.MissingSamples));
                }

                // Even with matching numbering we apply the map: palettes may contain
                // ids that are not in the server registry (gaps from deleted blocks) —
                // they must likewise be reduced to the fallback block.
                options.BlockIdMap = built.Map;
                report.BlockIdsTranslated = true;
            }
        }

        if (options.BlockIdMap is not { Count: > 0 } && !options.WriteBlockIds)
        {
            report.Warnings.Add(
                "Id translation is not set and BlockIDs is disabled — blocks in the world will have foreign numbering. "
                + "Specify a local registry (LocalRegistry / --local-registry).");
        }
    }

    /// <summary>
    /// The table for gamedata.ModData["BlockIDs"]: the description of the numbering the
    /// chunks are written in. Translated — the target registry; not translated — the
    /// capture's server registry.
    /// </summary>
    private static BlockRegistryTable? ResolveBlockIdsTable(CaptureModel model, BuildOptions options,
        BuildReport report, Action<string> log)
    {
        if (!options.WriteBlockIds) return null;

        if (options.BlockIdMap is { Count: > 0 })
        {
            if (options.LocalRegistry is { Count: > 0 })
            {
                log($"BlockIDs: the target registry of the translation — {options.LocalRegistry.Count} id → code mappings"
                    + " (the game needs it to learn what the ids in the world mean).");
                return options.LocalRegistry;
            }

            report.Warnings.Add(
                "The ids are translated, but the registry they were translated into is unknown, so the world cannot "
                + "describe its own numbering. Pass it (LocalRegistry / --local-registry): without the table the game "
                + "reads the chunks with its own block numbering and the blocks come out wrong.");
            return null;
        }

        var server = model.BlockRegistry();
        if (server.Count == 0)
        {
            report.Warnings.Add("No block registry (ServerAssets) in the capture — BlockIDs not written.");
            return null;
        }

        log($"BlockIDs: the server registry — {server.Count} id → code mappings.");
        return BlockRegistryTable.FromDictionary(server);
    }

    /// <summary>Find the placeholder block id for missing blocks in the local registry.</summary>
    private static int ResolveMissingBlockId(BuildOptions options, BuildReport report, Action<string> log)
    {
        string? code = options.MissingBlockCode;
        if (string.IsNullOrWhiteSpace(code) || options.LocalRegistry == null) return 0;

        // The code may be given both as "rock-granite" and as "game:rock-granite" —
        // compare in normalized form, otherwise the placeholder would silently not be found.
        string wanted = BlockRegistryTable.NormalizeCode(code);
        foreach (var pair in options.LocalRegistry.Blocks)
        {
            if (!string.Equals(BlockRegistryTable.NormalizeCode(pair.Value), wanted, StringComparison.Ordinal)) continue;
            log($"Blocks missing locally are replaced with \"{code}\" (id {pair.Key}).");
            return pair.Key;
        }

        report.Warnings.Add(
            $"Placeholder block \"{code}\" not found in the local registry — missing blocks will become air.");
        return 0;
    }

    /// <summary>
    /// Decor (grass, flowers) arrives in the packet as (index3d, blockId) pairs.
    /// ServerChunk.FastSerializeDecors writes them from Decors, taking only BlockId,
    /// so lightweight Block objects with the right id are enough.
    /// </summary>
    private static List<(int Index3d, int BlockId)> BuildDecors(ChunkPayload packet, Dictionary<int, int>? idMap)
    {
        int count = Math.Min(packet.DecorsPos.Length, packet.DecorsIds.Length);
        if (count == 0) return [];

        var decors = new List<(int, int)>(count);
        for (int i = 0; i < count; i++)
        {
            int id = packet.DecorsIds[i];
            if (idMap != null)
            {
                // An id that is not in the map is reduced to air — the same as the
                // palette translation does. Otherwise a foreign server id would get into the world.
                id = idMap.TryGetValue(id, out int local) ? local : 0;
            }
            decors.Add((packet.DecorsPos[i], id));
        }
        return decors;
    }

    /// <summary>
    /// The mapchunk table row. Mandatory: without it the
    /// ServerSystemSupplyChunks gate will not read our chunks.
    /// </summary>
    private static byte[] BuildMapChunkBlob(MapChunkPayload packet, BuildOptions options)
    {
        ushort[]? rain = ByteToUshort(packet.RainHeightMap);
        ushort[]? terrain = ByteToUshort(packet.TerrainHeightMap);

        // FromBytes does a RainHeightMap.Clone(), so rain must not be null.
        rain ??= new ushort[1024];
        terrain ??= (ushort[])rain.Clone();

        var mapChunk = new ServerMapChunk
        {
            RainHeightMap = rain,
            WorldGenTerrainHeightMap = terrain,
            YMax = (ushort)packet.Ymax,
            WorldGenVersion = options.WorldGenVersion,
            CurrentIncompletePass = EnumWorldGenPass.Done,
            TopRockIdMap = new int[1024],
            SedimentaryThicknessMap = new ushort[1024],
            CaveHeightDistort = new byte[1024],
            Moddata = new Dictionary<string, byte[]>()
        };

        return mapChunk.ToBytes();
    }

    /// <summary>
    /// An exact counterpart of the internal ArrayConvert.ByteToUshort: the game does
    /// a raw memory copy, that is, a little-endian reinterpretation.
    /// </summary>
    private static ushort[]? ByteToUshort(byte[]? data)
    {
        if (data == null || data.Length < 2) return null;
        var span = MemoryMarshal.Cast<byte, ushort>(data.AsSpan(0, data.Length - data.Length % 2));
        return span.ToArray();
    }

    /// <summary>
    /// Write the capture's block registry into gamedata.ModData["BlockIDs"].
    /// The game reads it on AssetsFirstLoaded, before chunks load, and moves live
    /// blocks to the saved ids. Without it, with a foreign mod set the blocks would
    /// silently end up being the wrong ones; with it — visible IsMissing placeholders.
    /// </summary>
    private static void WriteBlockIds(SQLiteDbConnectionv2 db, Dictionary<int, string> registry)
    {
        byte[] raw = db.GetGameData();
        if (raw == null || raw.Length == 0)
            throw new InvalidDataException("No gamedata row in the template world — is this a Vintage Story save?");

        SaveGame save = SerializerUtil.Deserialize<SaveGame>(raw);
        save.ModData ??= new ConcurrentDictionary<string, byte[]>(4, 16);
        save.ModData[BlockIdsKey] = SerializerUtil.Serialize(registry);
        db.StoreGameData(SerializerUtil.Serialize(save));
    }

    /// <summary>
    /// Raise SaveGame.LastEntityId to the highest captured entity id: the game does not
    /// restore the counter from chunk rows, so a newly spawned entity would otherwise
    /// collide with a captured id and be renumbered with a warning in the log.
    /// </summary>
    /// <summary>
    /// gamedata of a template build. The template keeps its own world configuration and
    /// block tables — that is the point of building onto one — but everything the capture
    /// knows about the save itself is applied here: the entity id counter (otherwise newly
    /// spawned entities collide with the captured ids and the server renumbers them) and
    /// the captured clock and player position.
    ///
    /// The clock and the spawn point go through the same methods a from-scratch build uses,
    /// so the two paths cannot drift apart. The spawn point of the template is left alone
    /// when the capture has no player position to replace it with.
    /// </summary>
    private static void UpdateTemplateGameData(SQLiteDbConnectionv2 db, CaptureModel model, Action<string> log)
    {
        byte[] raw = db.GetGameData();
        if (raw == null || raw.Length == 0) return;

        SaveGame save = SerializerUtil.Deserialize<SaveGame>(raw);

        bool changed = false;

        long maxId = model.HighestEntityId;
        if (maxId > save.LastEntityId)
        {
            save.LastEntityId = maxId;
            changed = true;
            log($"Entity id counter: starts after {save.LastEntityId} (the captured entities keep their ids)");
        }

        if (model.WorldState != null)
        {
            TemplateFactory.ApplyWorldState(save, model, log);
            if (model.WorldState.HasPlayer) TemplateFactory.ApplySpawn(save, model, log);
            changed = true;
        }

        // Re-serializing gamedata is what every save of the game does, but there is no reason
        // to touch the template's own bytes when the capture says nothing new about them.
        if (!changed) return;

        db.StoreGameData(SerializerUtil.Serialize(save));
    }
}
