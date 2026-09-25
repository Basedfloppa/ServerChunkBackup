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
    /// IMPORTANT: BlockIDs must not be written together with translation — otherwise
    /// the game would move the blocks a second time, on top of the already translated
    /// ones. <see cref="WorldBuilder"/> disables <see cref="WriteBlockIds"/> itself
    /// when translation is set.
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
    /// Write the block registry from the capture into gamedata.ModData["BlockIDs"].
    ///
    /// Only needed when ids are NOT translated (that is, <see cref="BlockIdMap"/> is
    /// not set): then the only chance is to ask the game to remap the blocks itself.
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
            TemplateFactory.CreateNew(outputPath, model, worldName, options.WriteBlockIds, log);
            log($"World created from scratch: {outputPath}");

            // The block registry has already been written into this save by TemplateFactory.
            report.BlockRegistrySize = options.WriteBlockIds ? model.Assets.Blocks.Count : 0;
            report.BlockIdsWritten = report.BlockRegistrySize > 0;
        }

        var pool = new StandaloneChunkDataPool();
        var codec = options.BlockIdMap is { Count: > 0 } ? new GameZstdCodec() : null;
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
                chunkRows.Add(new DbChunk(
                    new ChunkPos(chunk.X, chunk.Y, chunk.Z, options.Dim),
                    BuildChunkBlob(chunk, pool, options.BlockIdMap, codec, report, serverRegistry, model)));
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

        if (report.BlockEntitiesDropped > 0)
        {
            report.Warnings.Add(
                $"{report.BlockEntitiesDropped} block entities failed to parse and were skipped — "
                + "the corresponding chiseled blocks, chests and machines will stay empty in the world.");
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
        if (options.WriteBlockIds && useTemplate)
        {
            var registry = model.BlockRegistry();
            report.BlockRegistrySize = registry.Count;
            if (registry.Count == 0)
            {
                report.Warnings.Add("No block registry (ServerAssets) in the capture — BlockIDs not written.");
            }
            else
            {
                WriteBlockIds(db, registry);
                report.BlockIdsWritten = true;
                log($"BlockIDs: wrote {registry.Count} id → code mappings");
            }
        }

        pool.FreeAll();

        if (chunkRows.Count > 0) db.SetChunks(chunkRows);
        if (mapChunkRows.Count > 0) db.SetMapChunks(mapChunkRows);

        log($"Chunks written: {report.ChunksWritten}, heightmaps: {report.MapChunksWritten}");

        // --- read-back check with the same game class ---
        var positions = chunkRows.Take(50).Select(r => r.Position).ToList();
        int readBack = db.GetChunks(positions).Count();
        log($"Read-back check: {readBack} of {positions.Count} control rows found");

        return report;
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
        IReadOnlyDictionary<int, string>? serverRegistry, CaptureModel model)
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
        var entries = BuildBlockEntityEntries(model.BlockEntitiesFor(packet), serverRegistry, idMap, report);

        var moddata = ReadModdata(packet);

        return ChunkBlobWriter.Write(
            blocks,
            packet.Light,
            packet.LightSat,
            liquids,
            entries,
            moddata,
            packet.LightPositions.Length > 0 ? packet.LightPositions : null,
            BuildDecors(packet, idMap),
            GameVersion.ShortGameVersion,
            packet.Empty);
    }

    /// <summary>
    /// Prepare block entity entries for the chunk row: translate ids inside the data
    /// (chiseled-block materials, decor) and pack them into the save format.
    /// </summary>
    private static List<byte[]> BuildBlockEntityEntries(
        List<BlockEntityPayload> blockEntities,
        IReadOnlyDictionary<int, string>? serverRegistry,
        Dictionary<int, int>? idMap,
        BuildReport report)
    {
        var entries = new List<byte[]>(blockEntities.Count);
        foreach (var be in blockEntities)
        {
            byte[] data = BlockEntityRewrite.Rewrite(be, serverRegistry, idMap, out var stats);
            report.BlockEntities++;
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

        // Translation and BlockIDs cannot go together: the game would move the blocks
        // a second time, on top of the translated ones, and everything would break.
        if (options.BlockIdMap is { Count: > 0 } && options.WriteBlockIds)
        {
            options.WriteBlockIds = false;
            log("Not writing BlockIDs: ids are translated in the data itself (otherwise the remapper would shift the blocks twice).");
        }

        if (options.BlockIdMap is not { Count: > 0 } && !options.WriteBlockIds)
        {
            report.Warnings.Add(
                "Id translation is not set and BlockIDs is disabled — blocks in the world will have foreign numbering. "
                + "Specify a local registry (LocalRegistry / --local-registry).");
        }
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
}
