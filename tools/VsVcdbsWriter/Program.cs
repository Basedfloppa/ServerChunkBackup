using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;
using VsChunkDump.Core;
using VsFullCapture;

namespace VsFullCaptureWriter;

/// <summary>
/// Offline writer that produces a real .vcdbs from a capture of server data.
///
/// Commands:
///   info    &lt;captureDir&gt;                                  — what the capture contains
///   build   &lt;captureDir&gt; --template &lt;world.vcdbs&gt; -o &lt;out&gt; — build the world
///   selftest                                             — end-to-end check without the game or a capture
/// </summary>
internal static class Program
{
    private const int ChunkSize = 32;
    private const int BlocksPerChunk = ChunkSize * ChunkSize * ChunkSize;

    private static string GameDir =>
        Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "";

    /// <summary>Half-written world of the current build: delete it on Ctrl+C.</summary>
    private static string? _buildingPath;

    /// <summary>
    /// The writer is not self-contained: game assemblies and native libraries come
    /// from the installed game, so without VINTAGE_STORY there is nothing to run on.
    /// </summary>
    private static bool EnsureGameDir()
    {
        if (!string.IsNullOrWhiteSpace(GameDir)
            && File.Exists(Path.Combine(GameDir, "VintagestoryLib.dll")))
        {
            return true;
        }

        Console.Error.WriteLine(string.IsNullOrWhiteSpace(GameDir)
            ? "VINTAGE_STORY is not set — the path to the Vintage Story installation directory."
            : $"There is no VintagestoryLib.dll in \"{GameDir}\" — check VINTAGE_STORY.");
        Console.Error.WriteLine("  export VINTAGE_STORY=/path/to/VintageStory");
        Console.Error.WriteLine("The game must be closed, and its Lib/ must be available for the native libraries.");
        return false;
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception) { /* not critical */ }
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

        AssemblyLoadContext.Default.Resolving += Resolve;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        if (!EnsureGameDir()) return 2;

        // An interrupt must not leave a half-written world behind (and must certainly
        // not delete an existing one: the target is only touched after a successful build).
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            string? work = _buildingPath;
            if (work != null)
            {
                foreach (string path in new[] { work, work + "-wal", work + "-shm", work + "-journal" })
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* exiting */ }
                }
                Console.Error.WriteLine("Interrupted: the half-written world was deleted, the existing one was left untouched.");
            }
            Environment.Exit(130);
        };

        try
        {
            return args[0] switch
            {
                "info" => Info(args.Skip(1).ToArray()),
                "build" => Build(args.Skip(1).ToArray()),
                "selftest" => SelfTest(),
                "codes" => Codes(args.Skip(1).ToArray()),
                "registry" => Registry(args.Skip(1).ToArray()),
                "check" => Check(args.Skip(1).ToArray()),
                "chisel" => Chisel(args.Skip(1).ToArray()),
                "verify" => Verify(args.Skip(1).ToArray()),
                _ => Fail("Unknown command: " + args[0])
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Error: " + e.Message);
            if (Environment.GetEnvironmentVariable("VSFULLCAPTURE_DEBUG") == "1") Console.Error.WriteLine(e);
            return 2;
        }
    }

    private static void PrintUsage() => Console.WriteLine("""
        vsfullcapture-writer — builds a real .vcdbs from a capture made by the VS Full Capture mod.

        Usage:
          vsfullcapture-writer info <captureDir>
          vsfullcapture-writer build <captureDir> --template <world.vcdbs> -o <out.vcdbs> [options]
          vsfullcapture-writer registry <world.vcdbs> [-o <registry.json>]
          vsfullcapture-writer check <captureDir> <world.vcdbs> --local-registry <registry.json>
          vsfullcapture-writer chisel <world.vcdbs> --local-registry <registry.json> [--dump n]
          vsfullcapture-writer verify <world.vcdbs> [how many chunks to check]
          vsfullcapture-writer selftest

        build options:
          -o, --out <file>    where to write the result (required)
          --template <file>   template world; optional — without it the save is created from
                              scratch from the captured parameters and the world configuration
          --name <name>       name of the new world (template world only)
          --dim <n>           dimension (default 0)
          --no-mapchunk       do not write mapchunk rows (the game then ignores the chunks)
          --no-blockids       do not write the block registry into gamedata
          --force             allow overwriting an existing -o file
                              (without it the writer refuses: overwrite is irreversible)

        Block id translation (otherwise the blocks in the world will be foreign):
          --local-registry <file>       block registry of this world as JSON (see the registry command)
          --local-registry-from <world> read that registry straight from the .vcdbs in place
          --missing-block <code>        what to replace blocks that are missing locally with
                                        (air by default; pass the same code to check)

        If no registry is given, ids are not translated: the tool writes BlockIDs and asks
        the game to rearrange the blocks with the remapper — this works, but on every world
        open it pushes the whole registry through the remapper and turns missing blocks into placeholders.

        The writer requires an installed game: export VINTAGE_STORY=/path/to/VintageStory
        (and its native Lib/libe_sqlite3.so, Lib/libzstd.so — via LD_LIBRARY_PATH).

        The check command compares block codes in the capture and in the built world: each
        palette entry on both sides must be the SAME block (different ids, same code).
        Only the application of the translation is verified; --local-registry itself is not
        validated: the wrong registry makes both build and check agree on the wrong blocks.

        The chisel command checks the translation inside block entities (materials of chiseled
        blocks and decor): it takes block entities straight from the world, translates them
        into the given registry, and verifies that every material resolves back to the same block.

        Important: the game must be closed and the template world must not be open.
        Full error stack: VSFULLCAPTURE_DEBUG=1.
        """);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    // ------------------------------------------------------------------ info

    private static int Info(string[] args)
    {
        if (args.Length < 1) return Fail("Specify a capture directory: info <captureDir>");
        string dir = args[0];

        var manifest = CaptureManifest.Load(dir);
        var (counts, totalBytes) = CaptureReader.Summarize(dir);

        Console.WriteLine($"Capture:         {Path.GetFullPath(dir)}");
        Console.WriteLine($"Format:          {manifest.Format} v{manifest.FormatVersion}");
        Console.WriteLine($"Game version:    {manifest.GameVersion ?? "unknown"}");
        Console.WriteLine($"Server:          {manifest.ServerName ?? "—"}");
        Console.WriteLine($"Started:         {manifest.StartedUtc:u}");
        Console.WriteLine($"Updated:         {manifest.UpdatedUtc:u}");
        Console.WriteLine($"Records:         {manifest.RecordsWritten} ({totalBytes / 1024.0 / 1024.0:F1} MiB of data)");
        Console.WriteLine();
        Console.WriteLine("By type:");
        foreach (CaptureRecordType type in Enum.GetValues<CaptureRecordType>())
        {
            if (counts.TryGetValue(type, out long n) && n > 0)
                Console.WriteLine($"  {CaptureFormat.Describe(type),-28} {n,8}");
        }
        Console.WriteLine();

        Console.Write("Reading capture");
        var model = CaptureModel.Load(dir, m => Console.WriteLine("\n" + m));
        Console.WriteLine($"\rRecords read: {model.RecordsRead}");
        if (manifest.RecordsWritten != model.RecordsRead)
        {
            Console.WriteLine($"NOTE: the manifest lists {manifest.RecordsWritten} records, but the file has "
                              + $"{model.RecordsRead} — the manifest may not have been updated (abnormal termination).");
        }

        if (model.Identification != null)
        {
            var id = model.Identification;
            Console.WriteLine($"World size:      X {id.MapSizeX}  Y {id.MapSizeY}  Z {id.MapSizeZ}");
            Console.WriteLine($"Seed:            {id.Seed}");
            Console.WriteLine($"RequireRemapping:{id.RequireRemapping}");
            Console.WriteLine($"Game version:    {id.GameVersion}");
            var mods = model.ServerMods();
            Console.WriteLine($"Server mods:     {mods.Count}" + (mods.Count > 0 ? " (" + string.Join(", ", mods.Take(8)) + ")" : ""));
        }
        else
        {
            Console.WriteLine("WARNING: the capture has no server identification packet.");
        }

        int sections = model.SectionsPerColumn;
        Console.WriteLine();
        Console.WriteLine($"Unique chunks:          {model.Chunks.Count}");
        Console.WriteLine($"Heightmaps (mapchunk):  {model.MapChunks.Count}");
        Console.WriteLine($"Block registry:         {model.BlockRegistry().Count} entries");
        if (model.Connections > 0)
        {
            Console.WriteLine($"Server connections:   {model.Connections}"
                              + (model.Connections > 1
                                  ? "  (the registry and configuration come from the last one — this may distort earlier sessions)"
                                  : ""));
        }

        if (model.Chunks.Count > 0)
        {
            var xs = model.Chunks.Keys.Select(k => k.X);
            var zs = model.Chunks.Keys.Select(k => k.Z);
            var ys = model.Chunks.Keys.Select(k => k.Y);
            Console.WriteLine($"Range: X {xs.Min()}..{xs.Max()}  Y {ys.Min()}..{ys.Max()}  Z {zs.Min()}..{zs.Max()}");
        }

        // How many columns are eligible for writing under the gate rules.
        int complete = 0, noMapChunk = 0, incomplete = 0;
        foreach (var column in model.Chunks.Values.GroupBy(c => (c.X, c.Z)))
        {
            var sectionsPresent = column.Select(c => c.Y).Distinct().ToList();
            bool isComplete = sectionsPresent.Count == sections && sectionsPresent.Min() == 0 && sectionsPresent.Max() == sections - 1;
            if (!isComplete) { incomplete++; continue; }
            if (!model.MapChunks.ContainsKey(column.Key)) { noMapChunk++; continue; }
            complete++;
        }

        Console.WriteLine();
        Console.WriteLine("Column readiness for the world build:");
        Console.WriteLine($"  complete columns with a heightmap: {complete}");
        Console.WriteLine($"  without a heightmap:               {noMapChunk}  (will be skipped)");
        Console.WriteLine($"  captured only in part:             {incomplete}  (will be skipped)");
        return 0;
    }

    // ----------------------------------------------------------------- build

    private static int Build(string[] args)
    {
        if (args.Length < 1) return Fail("Specify a capture directory: build <captureDir> --template <world.vcdbs> -o <out.vcdbs>");
        string dir = args[0];

        var options = new BuildOptions();
        string? registryPath = null;
        string? registryFromSave = null;
        string? missingBlockCode = null;
        bool force = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--template": options.TemplatePath = Next(args, ref i); break;
                case "-o" or "--out": options.OutputPath = Next(args, ref i); break;
                case "--dim": options.Dim = int.Parse(Next(args, ref i)); break;
                case "--name": options.WorldName = Next(args, ref i); break;
                case "--no-mapchunk": options.WriteMapChunks = false; break;
                case "--no-blockids": options.WriteBlockIds = false; break;
                case "--local-registry": registryPath = Next(args, ref i); break;
                case "--local-registry-from": registryFromSave = Next(args, ref i); break;
                case "--missing-block": missingBlockCode = Next(args, ref i); break;
                case "--force": force = true; break;
                default: return Fail("Unknown option: " + args[i]);
            }
        }

        if (string.IsNullOrEmpty(options.OutputPath)) return Fail("Missing -o/--out");
        options.Force = force;

        if (!File.Exists(Path.Combine(dir, CaptureFormat.FileName)))
            return Fail($"In \"{dir}\" there is no {CaptureFormat.FileName} — this is not a capture directory.");

        if (!options.WriteMapChunks)
        {
            Console.WriteLine("WARNING: --no-mapchunk: without mapchunk rows the game will silently ignore "
                              + "every written chunk. The resulting world will be empty.");
        }

        if (registryPath != null && registryFromSave != null)
            return Fail("Give only one of: --local-registry or --local-registry-from");

        if (registryPath != null)
        {
            options.LocalRegistry = BlockRegistryTable.Load(registryPath);
            Console.WriteLine($"Local registry: {registryPath} ({options.LocalRegistry.Count} blocks)");
        }
        else if (registryFromSave != null)
        {
            options.LocalRegistry = ReadRegistryFromSave(registryFromSave)
                ?? throw new InvalidDataException(
                    $"There is no gamedata.ModData[\"BlockIDs\"] in \"{registryFromSave}\": the game writes that table when "
                    + "it opens a world, and a world with translated ids does not have it by construction. "
                    + "Take the registry from another one of your worlds or from the snapshot the mod makes.");
            Console.WriteLine($"Local registry read from \"{registryFromSave}\" ({options.LocalRegistry.Count} blocks)");
        }

        if (missingBlockCode != null) options.MissingBlockCode = missingBlockCode;

        Console.WriteLine("Reading capture...");
        var model = CaptureModel.Load(dir, m => Console.WriteLine(m));
        Console.WriteLine($"Records: {model.RecordsRead}, chunks: {model.Chunks.Count}, "
                          + $"heightmaps: {model.MapChunks.Count}"
                          + (model.Connections > 0 ? $", connections: {model.Connections}" : ""));

        string? captureVersion = model.Identification?.GameVersion;
        if (!string.IsNullOrEmpty(captureVersion)
            && !string.Equals(captureVersion, Vintagestory.API.Config.GameVersion.ShortGameVersion,
                StringComparison.Ordinal))
        {
            Console.WriteLine($"WARNING: the capture was taken on version {captureVersion}, but "
                              + $"{Vintagestory.API.Config.GameVersion.ShortGameVersion} is installed — "
                              + "the packet formats may not match.");
        }
        Console.WriteLine();

        _buildingPath = options.OutputPath + ".building";
        BuildReport report;
        try
        {
            report = WorldBuilder.Build(model, options, Console.WriteLine);
        }
        finally
        {
            _buildingPath = null;
        }

        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine($"  columns in capture:   {report.ColumnsCaptured}");
        Console.WriteLine($"  columns written:      {report.ColumnsWritten}");
        Console.WriteLine($"  chunks written:       {report.ChunksWritten}");
        Console.WriteLine($"  heightmaps written:   {report.MapChunksWritten}");
        Console.WriteLine($"  BlockIDs written:     {(report.BlockIdsWritten ? "yes, " + report.BlockRegistrySize + " entries" : "no")}");
        if (report.BlockIdsTranslated)
        {
            Console.WriteLine($"  block ids translated: yes — {report.IdTranslation ?? "map given manually"}");
            Console.WriteLine($"  palettes: entries {report.PaletteEntries:N0}, changed {report.PaletteChanged:N0}, "
                              + $"to the fallback block {report.PaletteFallback:N0} "
                              + $"(compressed layers {report.PaletteCompressedLayers}, raw {report.PaletteRawLayers})");
        }
        Console.WriteLine($"  block entities:       {report.BlockEntities:N0} "
                          + $"(rewritten {report.BlockEntitiesRewritten:N0}, "
                          + $"dropped {report.BlockEntitiesDropped:N0})");
        if (report.MaterialsAsCodes > 0 || report.MaterialsUnknown > 0)
        {
            Console.WriteLine($"  chiseled materials:   translated to codes {report.MaterialsAsCodes:N0}, "
                              + $"not found in the server registry {report.MaterialsUnknown:N0}");
        }
        if (report.RequireRemapping)
            Console.WriteLine("  WARNING: the server reported RequireRemapping=1 — its mod set differs from the reference one.");

        foreach (string warning in report.Warnings) Console.WriteLine("  ! " + warning);

        Console.WriteLine();
        Console.WriteLine($"Done: {options.OutputPath}");
        Console.WriteLine("Open this world in the game. If the blocks look wrong, compare the mod set with the server.");
        return report.ChunksWritten > 0 ? 0 : 1;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException("Missing value for " + args[i]);
        return args[++i];
    }

    /// <summary>
    /// Open a SQLite connection through the connection string builder: manual
    /// concatenation breaks when the path contains a ";".
    /// </summary>
    private static Microsoft.Data.Sqlite.SqliteConnection OpenSqlite(string path)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path };
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    // ----------------------------------------------------------------- codes

    /// <summary>Show the block registry from the capture: how codes are written and whether there are gaps in the ids.</summary>
    private static int Codes(string[] args)
    {
        if (args.Length < 1) return Fail("Specify a capture directory: codes <captureDir> [how many to show]");
        int limit = args.Length > 1 ? int.Parse(args[1]) : 20;

        var model = CaptureModel.Load(args[0]);
        var registry = model.BlockRegistry();
        if (registry.Count == 0) return Fail("The capture has no block registry.");

        int maxId = registry.Keys.Max();
        var samples = registry.OrderBy(kv => kv.Key).ToList();

        Console.WriteLine($"Entries: {registry.Count}, maximum id: {maxId}");
        Console.WriteLine();
        Console.WriteLine("First entries:");
        foreach (var entry in samples.Take(limit))
            Console.WriteLine($"  {entry.Key,-7} \"{entry.Value}\"");

        Console.WriteLine();
        Console.WriteLine("What the codes look like:");
        int withDomain = registry.Values.Count(v => v.Contains(':'));
        Console.WriteLine($"  with a domain (mod:path): {withDomain}");
        Console.WriteLine($"  without a domain (path):  {registry.Count - withDomain}");

        // Id continuity: gaps mean that part of the registry never arrived.
        // maxId comes from the data and can be anything — clamp it to a reasonable limit.
        int gaps = 0;
        if (maxId >= 0 && maxId <= 5_000_000)
        {
            for (int id = 0; id <= maxId; id++) if (!registry.ContainsKey(id)) gaps++;
        }
        else
        {
            Console.WriteLine($"  (the registry has a suspicious maximum id {maxId} — not counting gaps)");
        }
        Console.WriteLine($"  missing ids (gaps):   {gaps}");
        return 0;
    }

    // -------------------------------------------------------------- registry

    /// <summary>
    /// Read the "block id → code" table out of a world's gamedata.ModData["BlockIDs"].
    ///
    /// This is exactly the registry the game uses to number blocks in THIS world: it is
    /// written by ServerSystemBlockIdRemapper itself when the world is created/opened.
    /// It is what yields a local registry file for id translation.
    ///
    /// Note: for a world that has already been opened with foreign ids the table is mixed
    /// (foreign ids first, then its own). Only a "dense" registry is usable — the one the
    /// game wrote for a world without BlockIDs.
    /// </summary>
    private static int Registry(string[] args)
    {
        if (args.Length < 1) return Fail("Specify a world: registry <world.vcdbs> [-o <registry.json>]");
        string path = args[0];
        string? output = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--out": output = Next(args, ref i); break;
                default: return Fail("Unknown option: " + args[i]);
            }
        }

        var table = ReadRegistryFromSave(path);
        if (table == null)
        {
            return Fail(
                $"There is no gamedata.ModData[\"BlockIDs\"] in \"{path}\" — the game itself writes that table when it opens "
                + "a world (ServerSystemBlockIdRemapper). A world built with id translation in the palettes does not have it "
                + "by construction. Take the registry from another one of your worlds or from the mod's snapshot: "
                + "<VintagestoryData>/FullCapture/localregistry.json.");
        }

        Console.WriteLine($"World:   {path}");
        Console.WriteLine($"Entries: {table.Count}, maximum id: {table.MaxId}, "
                          + $"{(table.IsDense ? "numbering is dense (registry read in full)" : "the numbering has gaps")}");
        Console.WriteLine("First entries:");
        foreach (var entry in table.Blocks.OrderBy(kv => kv.Key).Take(8))
            Console.WriteLine($"  {entry.Key,-7} \"{entry.Value}\"");

        if (output == null) return 0;

        table.Save(output, "from " + Path.GetFileName(path));
        Console.WriteLine();
        Console.WriteLine($"Written: {output}");
        Console.WriteLine("Usage: build <captureDir> --local-registry " + Path.GetFileName(output) + " -o <world.vcdbs>");
        return 0;
    }

    /// <summary>Read the BlockIDs table from a world's gamedata. null if it is not there.</summary>
    private static BlockRegistryTable? ReadRegistryFromSave(string worldPath)
    {
        if (!File.Exists(worldPath)) throw new FileNotFoundException("File not found: " + worldPath);

        SQLitePCL.Batteries_V2.Init();
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(worldPath, ref error, requireWriteAccess: false,
                corruptionProtection: false, doIntegrityCheck: false))
        {
            throw new InvalidOperationException("Could not open the world: " + error);
        }

        byte[] raw = db.GetGameData();
        if (raw == null || raw.Length == 0) return null;

        SaveGame save = SerializerUtil.Deserialize<SaveGame>(raw);
        if (save.ModData == null || !save.ModData.TryGetValue("BlockIDs", out byte[] blockIds) || blockIds.Length == 0)
            return null;

        var registry = SerializerUtil.Deserialize<Dictionary<int, string>>(blockIds);
        if (registry == null || registry.Count == 0) return null;

        var table = BlockRegistryTable.FromDictionary(registry);
        table.GameVersion = save.LastSavedGameVersion ?? save.CreatedGameVersion;
        return table;
    }

    // ----------------------------------------------------------------- check

    /// <summary>
    /// Compare block codes between the capture and the built world.
    ///
    /// The idea: the bit planes in the blob store palette INDICES, not ids. So after id
    /// translation a palette entry with the same index must mean the same block. We
    /// compute the expected translation (server id → local id) and compare it with what
    /// actually lies in the world. Blocks that do not exist locally must become the
    /// fallback (air) — that is not an error but the declared behaviour.
    /// </summary>
    private static int Check(string[] args)
    {
        if (args.Length < 2)
            return Fail("Specify a capture and a world: check <captureDir> <world.vcdbs> --local-registry <registry.json>");

        string captureDir = args[0];
        string worldPath = args[1];
        string? registryPath = null;
        string? missingBlockCode = null;
        int limit = int.MaxValue;

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--local-registry": registryPath = Next(args, ref i); break;
                case "--missing-block": missingBlockCode = Next(args, ref i); break;
                case "--limit": limit = int.Parse(Next(args, ref i)); break;
                default: return Fail("Unknown option: " + args[i]);
            }
        }

        if (registryPath == null) return Fail("--local-registry is required: without it there is nothing to compare the codes against.");
        if (!File.Exists(worldPath))
            return Fail($"There is no world file \"{worldPath}\" — nothing to check (the world is not created anew).");

        var local = BlockRegistryTable.Load(registryPath);
        var model = CaptureModel.Load(captureDir);
        var server = model.BlockRegistry();
        if (server.Count == 0) return Fail("The capture has no block registry — nothing to compare.");

        // The fallback for missing blocks must match the one used during the build,
        // otherwise check reports mismatches on every missing block.
        int fallbackId = 0;
        if (!string.IsNullOrWhiteSpace(missingBlockCode))
        {
            string wanted = BlockRegistryTable.NormalizeCode(missingBlockCode);
            foreach (var pair in local.Blocks)
            {
                if (!string.Equals(BlockRegistryTable.NormalizeCode(pair.Value), wanted, StringComparison.Ordinal)) continue;
                fallbackId = pair.Key;
                break;
            }
        }

        var expected = BlockIdMapBuilder.Build(server, local.Blocks, fallbackId);

        SQLitePCL.Batteries_V2.Init();
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(worldPath, ref error, false, false, false))
            return Fail("Could not open the world: " + error);

        // Read all chunk rows directly: the game's GetChunks works on 6000 positions,
        // but here all we need is a position → ServerChunk map.
        var rows = new Dictionary<(int X, int Y, int Z), ServerChunk>();
        using (var conn = OpenSqlite(worldPath))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT position, data FROM chunk";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pos = ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)reader.GetInt64(0)));
                var blob = (byte[])reader.GetValue(1);
                rows[(pos.X, pos.Y, pos.Z)] = SerializerUtil.Deserialize<ServerChunk>(blob);
            }
        }

        var codec = new GameZstdCodec();
        int checkedChunks = 0, checkedLayers = 0, mismatches = 0, missingChunks = 0, fallbackEntries = 0;
        var examples = new List<string>();

        foreach (var pair in model.Chunks.OrderBy(k => k.Key.Z).ThenBy(k => k.Key.X).ThenBy(k => k.Key.Y))
        {
            if (checkedChunks >= limit) break;
            var key = pair.Key;
            if (!rows.TryGetValue(key, out var worldChunk)) { missingChunks++; continue; }

            checkedChunks++;
            checkedLayers += CompareLayer("blocks", pair.Value.Blocks, worldChunk.blocksCompressed);
            checkedLayers += CompareLayer("liquids", pair.Value.Liquids, worldChunk.fluidsCompressed);

            int CompareLayer(string name, byte[]? captureBlob, byte[]? worldBlob)
            {
                if (captureBlob == null || worldBlob == null) return 0;
                if (!CombinedLayerBlob.TryReadPalette(captureBlob, codec, out int[]? sourcePalette, out _, out _)) return 0;
                if (!CombinedLayerBlob.TryReadPalette(worldBlob, codec, out int[]? worldPalette, out _, out _))
                {
                    mismatches++;
                    if (examples.Count < 8) examples.Add($"{key}: the \"{name}\" layer could not be parsed in the world");
                    return 1;
                }

                if (sourcePalette!.Length != worldPalette!.Length)
                {
                    mismatches++;
                    if (examples.Count < 8)
                        examples.Add($"{key}: the \"{name}\" layer — palette entries {sourcePalette.Length} vs {worldPalette.Length}");
                    return 1;
                }

                for (int i = 0; i < sourcePalette.Length; i++)
                {
                    int serverId = sourcePalette[i];
                    if (!expected.Map.TryGetValue(serverId, out int wantLocal)) wantLocal = expected.FallbackId;

                    // How many palette entries point at blocks that are not present here.
                    if (wantLocal == expected.FallbackId)
                    {
                        string serverCode = Code(server, serverId);
                        if (serverCode != Code(local.Blocks, expected.FallbackId)) fallbackEntries++;
                    }

                    if (worldPalette[i] == wantLocal) continue;
                    mismatches++;
                    if (examples.Count < 8)
                    {
                        examples.Add($"{key}: layer \"{name}\" entry {i}: id {serverId} "
                                     + $"\"{Code(server, serverId)}\" should have become {wantLocal} "
                                     + $"\"{Code(local.Blocks, wantLocal)}\", but became {worldPalette[i]} "
                                     + $"\"{Code(local.Blocks, worldPalette[i])}\"");
                    }
                    break;
                }
                return 1;
            }
        }

        static string Code(IReadOnlyDictionary<int, string> registry, int id)
            => registry.TryGetValue(id, out string? code) ? BlockRegistryTable.NormalizeCode(code) : "(none)";

        Console.WriteLine($"Capture:  {captureDir}");
        Console.WriteLine($"World:    {worldPath}");
        Console.WriteLine($"Registry: {registryPath} ({local.Count} blocks)");
        Console.WriteLine($"Translation: {expected.Describe()}");
        Console.WriteLine();
        Console.WriteLine($"Chunks compared: {checkedChunks}, palette layers: {checkedLayers}");
        if (missingChunks > 0) Console.WriteLine($"Capture chunks missing from the world (incomplete columns): {missingChunks}");
        Console.WriteLine($"Palette entries without a local block (became air): {fallbackEntries}");

        if (examples.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Examples of mismatches:");
            foreach (string line in examples) Console.WriteLine("  " + line);
        }

        bool ok = mismatches == 0 && checkedChunks > 0 && missingChunks == 0;
        Console.WriteLine();
        if (checkedChunks == 0)
        {
            Console.WriteLine("RESULT: no checkable chunk was found in the world — check failed.");
        }
        else if (missingChunks > 0)
        {
            Console.WriteLine($"RESULT: {missingChunks} capture chunks are missing from the world (check failed).");
        }
        else
        {
            Console.WriteLine(mismatches == 0
                ? "RESULT: the block codes in the world match the capture — the translation was applied correctly "
                  + "(the registry itself is not checked: it is taken from --local-registry as is)."
                : $"RESULT: {mismatches} mismatches.");
        }
        return ok ? 0 : 1;
    }

    // ----------------------------------------------------------------- chisel

    /// <summary>
    /// Check id translation inside block entities on real data.
    ///
    /// We take block entities straight from a world (it has its own BlockIDs table —
    /// the "source registry"), translate their data into the given local registry with
    /// the same code the build uses, and verify the main thing: after translation every
    /// material of a chiseled block resolves to a block with the SAME code as the
    /// original id. That means the shape and the materials of the block will not drift.
    /// </summary>
    private static int Chisel(string[] args)
    {
        if (args.Length < 1) return Fail("Specify a world: chisel <world.vcdbs> --local-registry <registry.json> [--dump n]");

        string worldPath = args[0];
        string? registryPath = null;
        int limit = int.MaxValue;
        int dump = 0;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--local-registry": registryPath = Next(args, ref i); break;
                case "--limit": limit = int.Parse(Next(args, ref i)); break;
                case "--dump": dump = int.Parse(Next(args, ref i)); break;
                default: return Fail("Unknown option: " + args[i]);
            }
        }

        if (registryPath == null) return Fail("--local-registry is required: it is the registry of the world we transfer the data into.");

        var local = BlockRegistryTable.Load(registryPath);

        // The source registry is the world's own BlockIDs table. Without it there is nothing to take.
        BlockRegistryTable? source = ReadRegistryFromSave(worldPath);
        if (source == null)
        {
            return Fail("The world has no gamedata.ModData[\"BlockIDs\"] — there is no way to describe the source ids. "
                        + "Use a world created by the game (not one built by this tool).");
        }

        var idMap = BlockIdMapBuilder.Build(source.Blocks, local.Blocks);
        Console.WriteLine($"Source:      {worldPath} ({source.Count} blocks)");
        Console.WriteLine($"Target:      {registryPath} ({local.Count} blocks)");
        Console.WriteLine($"Translation: {idMap.Describe()}");
        Console.WriteLine();

        SQLitePCL.Batteries_V2.Init();
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(worldPath, ref error, false, false, false))
            return Fail("Could not open the world: " + error);

        var positions = new List<ChunkPos>();
        using (var conn = OpenSqlite(worldPath))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT position FROM chunk";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                positions.Add(ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)reader.GetInt64(0))));
        }

        int totalBe = 0, chiselBe = 0, materialsChecked = 0, mismatches = 0, unparsed = 0, dumped = 0;
        var examples = new List<string>();

        foreach (var blob in db.GetChunks(positions))
        {
            if (totalBe >= limit) break;
            var chunk = SerializerUtil.Deserialize<ServerChunk>(blob);
            foreach (byte[] entry in ReadSerializedBlockEntities(chunk))
            {
                totalBe++;
                BlockEntityPayload payload;
                try
                {
                    using var ms = new MemoryStream(entry);
                    using var r = new BinaryReader(ms);
                    string classname = r.ReadString();
                    var tree = new TreeAttribute();
                    tree.FromBytes(r);
                    payload = new BlockEntityPayload
                    {
                        Classname = classname,
                        Data = tree.ToBytes()
                    };
                    if (tree["materials"] is not IntArrayAttribute before) continue;

                    chiselBe++;
                    byte[] rewritten = BlockEntityRewrite.Rewrite(payload, source.Blocks, idMap.Map, out _);

                    var after = new TreeAttribute();
                    after.FromBytes(rewritten);
                    if (after["materials"] is not StringArrayAttribute codes)
                    {
                        mismatches++;
                        if (examples.Count < 8) examples.Add($"{classname}: the materials are not codes after translation");
                        continue;
                    }

                    var original = (IntArrayAttribute)tree["materials"];
                    if (codes.value.Length != original.value.Length)
                    {
                        mismatches++;
                        if (examples.Count < 8) examples.Add($"{classname}: {original.value.Length} materials vs {codes.value.Length}");
                        continue;
                    }

                    for (int i = 0; i < codes.value.Length; i++)
                    {
                        materialsChecked++;
                        int sourceId = original.value[i];
                        string want = BlockRegistryTable.NormalizeCode(
                            source.Blocks.TryGetValue(sourceId, out string? c) ? c : "");
                        // The code from the data resolves in the target registry.
                        int resolvedId = ResolveByCode(local, codes.value[i]);
                        string got = resolvedId < 0
                            ? "(none)"
                            : BlockRegistryTable.NormalizeCode(local.Blocks[resolvedId]);
                        if (want.Length == 0 || got == want) continue;
                        mismatches++;
                        if (examples.Count < 8)
                            examples.Add($"{classname}: id {sourceId} \"{want}\" → \"{codes.value[i]}\" → \"{got}\"");
                    }

                    if (dump > 0 && dumped++ < dump)
                    {
                        Console.WriteLine($"{classname}: {codes.value.Length} materials, for example "
                                          + string.Join(", ", codes.value.Take(5)));
                    }
                }
                catch (Exception e)
                {
                    unparsed++;
                    if (examples.Count < 8) examples.Add("unparsed: " + e.Message);
                }
            }
        }

        Console.WriteLine($"Block entities: {totalBe}, of them chiseled with materials: {chiselBe}");
        Console.WriteLine($"Materials checked: {materialsChecked:N0}, mismatches: {mismatches}"
                          + (unparsed > 0 ? $", unparsed {unparsed}" : ""));

        if (examples.Count > 0)
        {
            Console.WriteLine();
            foreach (string line in examples) Console.WriteLine("  " + line);
        }

        Console.WriteLine();
        Console.WriteLine(mismatches == 0 && chiselBe > 0
            ? "RESULT: the materials of chiseled blocks translate into codes without loss."
            : mismatches == 0
                ? "Chiseled block entities were not found in the sample — nothing to check (this is not a success)."
                : $"RESULT: {mismatches} mismatches.");
        if (mismatches > 0) return 1;
        if (chiselBe == 0) return 1;
        if (unparsed > 0) return 1;
        return 0;
    }

    /// <summary>Find a block id by code (normalizing with or without the domain).</summary>
    private static int ResolveByCode(BlockRegistryTable table, string code)
    {
        string want = BlockRegistryTable.NormalizeCode(code);
        foreach (var pair in table.Blocks)
        {
            if (BlockRegistryTable.NormalizeCode(pair.Value) == want) return pair.Key;
        }
        return -1;
    }

    // ---------------------------------------------------------------- verify

    /// <summary>
    /// Verify a built world: reads the rows with the game class, parses the chunks and
    /// makes sure every column has the mandatory mapchunk row. A missing mapchunk is
    /// exactly what the game silently ignores, so this is the main check that a build
    /// is correct.
    /// </summary>
    private static int Verify(string[] args)
    {
        if (args.Length < 1) return Fail("Specify a world: verify <world.vcdbs> [how many chunks to check]");
        string path = args[0];
        if (!File.Exists(path)) return Fail("File not found: " + path);
        int sample = args.Length > 1 ? int.Parse(args[1]) : 200;

        SQLitePCL.Batteries_V2.Init();
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(path, ref error, requireWriteAccess: false, corruptionProtection: false, doIntegrityCheck: false))
            return Fail("Could not open the world: " + error);

        var chunkColumns = new HashSet<(int X, int Z)>();
        var mapChunkColumns = new HashSet<(int X, int Z)>();
        var chunkPositions = new List<ChunkPos>();

        using (var conn = OpenSqlite(path))
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT position FROM chunk";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var pos = ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)reader.GetInt64(0)));
                    chunkColumns.Add((pos.X, pos.Z));
                    chunkPositions.Add(pos);
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT position FROM mapchunk";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var pos = ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)reader.GetInt64(0)));
                    mapChunkColumns.Add((pos.X, pos.Z));
                }
            }
        }

        Console.WriteLine($"World: {path} ({new FileInfo(path).Length / 1024.0 / 1024.0:F1} MiB)");
        Console.WriteLine($"chunk rows:    {chunkPositions.Count} ({chunkColumns.Count} columns)");
        Console.WriteLine($"mapchunk rows: {mapChunkColumns.Count}");
        Console.WriteLine();

        int failures = 0;

        // 1. Every column with chunks must have a mapchunk row — otherwise the game
        //    silently regenerates the column and our blocks disappear.
        var missing = chunkColumns.Where(c => !mapChunkColumns.Contains(c)).ToList();
        if (missing.Count == 0)
        {
            Console.WriteLine("OK: every column has a mapchunk row — the game will read our chunks");
        }
        else
        {
            Console.WriteLine($"FAIL: {missing.Count} columns without a mapchunk, for example {string.Join("; ", missing.Take(5))}");
            Console.WriteLine("       the game will ignore such columns and generate them anew");
            failures++;
        }

        // 2. Columns must be written in full: all sections along Y. Otherwise the game
        //    may not see part of the world.
        int expectedSections = 0;
        try
        {
            byte[]? rawGameData = db.GetGameData();
            if (rawGameData is { Length: > 0 })
            {
                SaveGame save = SerializerUtil.Deserialize<SaveGame>(rawGameData);
                expectedSections = Math.Max(1, save.MapSizeY / 32);
            }
        }
        catch (Exception)
        {
            // At least check that the number of sections per column is uniform.
        }

        var byColumn = chunkPositions.GroupBy(p => (p.X, p.Z)).ToList();
        var sectionCounts = byColumn.Select(g => g.Select(p => p.Y).Distinct().Count()).Distinct().OrderBy(x => x).ToList();
        Console.WriteLine($"Sections per column: {string.Join(", ", sectionCounts)}"
                          + (expectedSections > 0 ? $" ({expectedSections} expected from MapSizeY)" : ""));

        if (expectedSections > 0)
        {
            var incomplete = byColumn.Where(g =>
            {
                var ys = g.Select(p => p.Y).Distinct().OrderBy(y => y).ToList();
                return ys.Count != expectedSections || ys[0] != 0 || ys[^1] != expectedSections - 1;
            }).ToList();

            if (incomplete.Count > 0)
            {
                Console.WriteLine($"FAIL: {incomplete.Count} columns were written with only some of their sections, "
                                  + $"for example {string.Join("; ", incomplete.Take(5).Select(g => g.Key))}");
                failures++;
            }
            else
            {
                Console.WriteLine("OK: every column was written in full (all Y sections)");
            }
        }

        // 3. The chunks must parse and contain blocks.
        var samplePositions = SpreadSample(chunkPositions, sample);
        var blobs = db.GetChunks(samplePositions).ToList();
        Console.WriteLine($"Chunks read with the game class: {blobs.Count} of {samplePositions.Count}");
        if (blobs.Count != samplePositions.Count)
        {
            Console.WriteLine("FAIL: some rows could not be read");
            failures++;
        }

        long totalBlocks = 0, nonAir = 0;
        int decodeErrors = 0;
        var pool = new StandaloneChunkDataPool();
        byte[] emptyModdata = SerializerUtil.Serialize(new Dictionary<string, byte[]>());

        foreach (byte[] blob in blobs)
        {
            try
            {
                // ServerChunk.FromBytes requires a real world: AfterDeserialization
                // resolves decor and block entities through it and crashes on null.
                // So we parse the protobuf directly and decode the blobs the client
                // way — it needs no world and checks exactly what the game will see.
                var raw = SerializerUtil.Deserialize<ServerChunk>(blob);
                var chunk = Vintagestory.Client.NoObf.ClientChunk.CreateNewCompressed(
                    pool, raw.blocksCompressed, raw.lightCompressed, raw.lightSatCompressed,
                    raw.fluidsCompressed, emptyModdata, raw.savedCompressionVersion);
                chunk.Unpack_ReadOnly();
                var data = chunk.Data;
                data.TakeBulkReadLock();
                try
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        totalBlocks++;
                        if (data.GetBlockId(i, Vintagestory.API.Common.BlockLayersAccess.SolidBlocks) != 0) nonAir++;
                    }
                }
                finally { data.ReleaseBulkReadLock(); }
            }
            catch (Exception e)
            {
                if (decodeErrors < 2) Console.WriteLine("  chunk parse error: " + e);
                decodeErrors++;
            }
        }

        Console.WriteLine($"Blocks checked: {totalBlocks:N0}, non-empty: {nonAir:N0} ({100.0 * nonAir / Math.Max(1, totalBlocks):F1}%)");
        if (decodeErrors > 0)
        {
            Console.WriteLine($"FAIL: {decodeErrors} chunks did not parse");
            failures++;
        }
        if (nonAir == 0)
        {
            Console.WriteLine("FAIL: all checked chunks are empty — the blocks were not written");
            failures++;
        }

        ReportBlockEntities(blobs);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "RESULT: the world was built correctly." : $"RESULT: {failures} problems.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Summary of block entities: their absence is why chiseled blocks and mod blocks
    /// rendered by a block entity show up empty.
    /// </summary>
    private static void ReportBlockEntities(IEnumerable<byte[]> blobs)
    {
        int chunks = 0, total = 0, withChisel = 0, materialsAsCodes = 0, materialsAsIds = 0,
            otherClassMaterials = 0, unparsed = 0;
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (byte[] blob in blobs)
        {
            var raw = SerializerUtil.Deserialize<ServerChunk>(blob);
            var entries = ReadSerializedBlockEntities(raw);
            if (entries.Count == 0) continue;
            chunks++;
            total += entries.Count;

            foreach (byte[] entry in entries)
            {
                try
                {
                    using var ms = new MemoryStream(entry);
                    using var r = new BinaryReader(ms);
                    string classname = r.ReadString();
                    classes[classname] = classes.GetValueOrDefault(classname) + 1;

                    var tree = new TreeAttribute();
                    tree.FromBytes(r);

                    if (tree["materials"] is IntArrayAttribute)
                    {
                        if (IsMicroBlock(classname)) { materialsAsIds++; withChisel++; }
                        else otherClassMaterials++;
                    }
                    else if (tree["materials"] is StringArrayAttribute)
                    {
                        if (IsMicroBlock(classname)) { materialsAsCodes++; withChisel++; }
                        else otherClassMaterials++;
                    }
                }
                catch (Exception)
                {
                    unparsed++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Block entities: {total:N0} in {chunks} chunks of the sample"
                          + (unparsed > 0 ? $", unparsed {unparsed}" : ""));
        if (total == 0)
        {
            Console.WriteLine("  The sample has no block entities. If this is a world we built, the capture");
            Console.WriteLine("  was taken by a build without support for them: chiseled blocks and everything");
            Console.WriteLine("  rendered by a block entity will stay empty there. A new capture from the server is needed.");
            return;
        }

        Console.WriteLine($"  MicroBlock materials: as codes {materialsAsCodes}, as ids {materialsAsIds}"
                          + (materialsAsIds > 0 ? "  (ids without translation — the blocks will be the wrong ones)" : ""));
        if (otherClassMaterials > 0)
            Console.WriteLine($"  other classes with a materials attribute: {otherClassMaterials} "
                              + "(left alone: the rewrite covers MicroBlock only)");
        foreach (var pair in classes.OrderByDescending(kv => kv.Value).Take(8))
            Console.WriteLine($"    {pair.Value,6}  {pair.Key}");
    }

    /// <summary>
    /// Read the serialized block entities from a chunk. The field is private, so this
    /// goes through reflection — the same way the game writes them into a save.
    /// </summary>
    private static List<byte[]> ReadSerializedBlockEntities(ServerChunk chunk)
    {
        var field = typeof(ServerChunk).GetField("BlockEntitiesSerialized",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(chunk) as List<byte[]> ?? [];
    }

    // -------------------------------------------------------------- selftest

    /// <summary>
    /// End-to-end check without running the game and without a real capture: build a
    /// synthetic chunk with the game's own classes, push it through the capture format,
    /// build a .vcdbs and read the blocks back with the game code.
    /// </summary>
    private static int SelfTest()
    {
        string work = Path.Combine(Path.GetTempPath(), "vsfullcapture-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        int failures = 0;

        try
        {
            Console.WriteLine("Selftest: capture -> .vcdbs -> read back with the game code");
            Console.WriteLine("Working directory: " + work);
            Console.WriteLine();

            string template = Path.Combine(work, "template.vcdbs");
            string captureDir = Path.Combine(work, "capture");
            string output = Path.Combine(work, "result.vcdbs");

            var pool = new StandaloneChunkDataPool();
            var expected = BuildPattern();
            var blockCodes = new[] { "game:air", "game:rock-granite", "game:soil-medium-normal", "game:grass-medium-normal" };

            Console.WriteLine("1. Template world with gamedata...");
            CreateTemplate(template, mapSizeY: 256);
            Console.WriteLine($"   {new FileInfo(template).Length} bytes");

            Console.WriteLine("2. Synthetic capture (8 sections of a column, mapchunk, block registry)...");
            WriteSyntheticCapture(captureDir, pool, expected, blockCodes);
            var (counts, bytes) = CaptureReader.Summarize(captureDir);
            Console.WriteLine($"   records {counts.Values.Sum()}, data {bytes} bytes");
            foreach (var kv in counts.OrderBy(k => k.Key)) Console.WriteLine($"     {CaptureFormat.Describe(kv.Key),-28} {kv.Value}");

            Console.WriteLine("3. Parsing the capture...");
            var model = CaptureModel.Load(captureDir, m => Console.WriteLine("   " + m));
            Console.WriteLine($"   chunks {model.Chunks.Count}, heightmaps {model.MapChunks.Count}, block registry {model.BlockRegistry().Count}");
            if (model.Chunks.Count != 8) { Console.WriteLine("   FAIL: expected 8 chunks"); failures++; }
            if (model.MapChunks.Count != 1) { Console.WriteLine("   FAIL: expected 1 heightmap"); failures++; }

            Console.WriteLine("4. Building the .vcdbs...");
            var report = WorldBuilder.Build(model, new BuildOptions
            {
                TemplatePath = template,
                OutputPath = output,
                WriteBlockIds = true // the mechanism is checked explicitly; it is off by default
            }, m => Console.WriteLine("   " + m));

            if (report.ChunksWritten != 8) { Console.WriteLine($"   FAIL: {report.ChunksWritten} chunks written, expected 8"); failures++; }
            if (report.MapChunksWritten != 1) { Console.WriteLine($"   FAIL: {report.MapChunksWritten} heightmaps written, expected 1"); failures++; }
            if (!report.BlockIdsWritten) { Console.WriteLine("   FAIL: BlockIDs was not written"); failures++; }

            Console.WriteLine("5. Reading the result with the game's SQLiteDbConnectionv2...");
            failures += VerifyOutput(output, expected, blockCodes);

            Console.WriteLine("6. Build without a template (the save is created from scratch)...");
            string directOutput = Path.Combine(work, "direct.vcdbs");
            var directReport = WorldBuilder.Build(model, new BuildOptions
            {
                OutputPath = directOutput,
                WorldName = "selftest-direct"
            }, m => Console.WriteLine("   " + m));

            if (directReport.ChunksWritten != 8) { Console.WriteLine($"   FAIL: {directReport.ChunksWritten} chunks, expected 8"); failures++; }
            if (!directReport.BlockIdsWritten)
            {
                // Chunk blobs store the server ids, so the id→code table is required:
                // without it the local ids will not match and the world will show the wrong blocks.
                Console.WriteLine("   FAIL: BlockIDs was not written");
                failures++;
            }
            failures += VerifyFreshSave(directOutput, expected, expectBlockIds: true);

            Console.WriteLine("7. Block id translation by rewriting the palette (a deliberately non-identity map)...");
            failures += TranslationTest(captureDir, work, expected, blockCodes);

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "RESULT: the end-to-end path capture -> .vcdbs is confirmed."
                : $"RESULT: {failures} failures.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception) { /* do not disturb the report */ }
        }
    }

    /// <summary>
    /// Check id translation by rewriting the palette: build a world with a deliberately
    /// NON-identity map (2 → 9, 3 → 1) and make sure that
    ///   1) the chunks already hold local ids,
    ///   2) BlockIDs is NOT written in that case (otherwise the game rearranges the blocks twice),
    ///   3) the block codes did not change: ids 2 and 9 are the same block.
    /// </summary>
    private static int TranslationTest(string captureDir, string work, int[] expected, string[] blockCodes)
    {
        int failures = 0;
        string output = Path.Combine(work, "translated.vcdbs");

        // Local registry: the same set of codes, but soil and grass have swapped ids
        // relative to the server ones. air and rock stayed in place.
        var local = BlockRegistryTable.FromDictionary(new Dictionary<int, string>
        {
            [0] = "game:air",
            [1] = "game:rock-granite",
            [9] = "game:soil-medium-normal",
            [4] = "game:grass-medium-normal"
        });
        local.Source = "selftest";

        var model = CaptureModel.Load(captureDir, null);
        var report = WorldBuilder.Build(model, new BuildOptions
        {
            OutputPath = output,
            WorldName = "selftest-translated",
            LocalRegistry = local
        }, m => Console.WriteLine("   " + m));

        if (!report.BlockIdsTranslated) { Console.WriteLine("   FAIL: id translation did not engage"); failures++; }
        if (report.BlockIdsWritten) { Console.WriteLine("   FAIL: BlockIDs was written together with id translation"); failures++; }
        if (report.PaletteEntries == 0) { Console.WriteLine("   FAIL: not a single palette was rewritten"); failures++; }

        Console.WriteLine($"   palette entries translated: {report.PaletteEntries}, "
                          + $"changed {report.PaletteChanged}, compressed layers {report.PaletteCompressedLayers}");

        // The reference blocks in the world must use the local numbering:
        // 1 (rock) → 1, 2 (soil) → 9, 3 (grass) → 4, 0 (air) → 0.
        var expectedLocal = new int[expected.Length];
        for (int i = 0; i < expected.Length; i++)
            expectedLocal[i] = expected[i] switch { 2 => 9, 3 => 4, _ => expected[i] };

        failures += VerifyOutputBlocks(output, expectedLocal, "id translation");

        // And the same by codes: read the chunks and compare the block codes with the source ones.
        failures += VerifyCodesUnchanged(captureDir, output, local);
        failures += VerifyBlockEntities(output, local);
        return failures;
    }

    /// <summary>
    /// Check of block entities in the built world: a chiseled block must arrive with
    /// its materials translated into CODES (otherwise the game picks foreign blocks)
    /// and with translated decor. Without block entities chiseled blocks are empty.
    /// </summary>
    private static int VerifyBlockEntities(string worldPath, BlockRegistryTable local)
    {
        int failures = 0;
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(worldPath, ref error, false, false, false))
        {
            Console.WriteLine("   FAIL: the world did not open: " + error);
            return 1;
        }

        ServerChunk? withBe = null;
        List<byte[]> entries = [];
        foreach (var cy in Enumerable.Range(0, 8))
        {
            var blob = db.GetChunks([new ChunkPos(0, cy, 0, 0)]).FirstOrDefault();
            if (blob == null) continue;
            var chunk = SerializerUtil.Deserialize<ServerChunk>(blob);
            var found = ReadSerializedBlockEntities(chunk);
            if (found.Count == 0) continue;
            withBe = chunk;
            entries = found;
            break;
        }

        if (entries.Count == 0)
        {
            Console.WriteLine("   FAIL: the world has no block entities — chiseled blocks will stay empty");
            return failures + 1;
        }

        Console.WriteLine($"   block entities in the world: {entries.Count}");

        foreach (byte[] entry in entries)
        {
            using var ms = new MemoryStream(entry);
            using var r = new BinaryReader(ms);
            string classname = r.ReadString();
            var tree = new TreeAttribute();
            tree.FromBytes(r);

            Check(classname == "MicroBlock", $"block entity class \"{classname}\"", ref failures);

            // Materials: they were server ids [2, 3] and must become codes.
            if (tree["materials"] is StringArrayAttribute codes)
            {
                Console.WriteLine("   chiseled materials: " + string.Join(", ", codes.value));
                Check(codes.value.Length == 2, $"{codes.value.Length} materials, expected 2", ref failures);
                if (codes.value.Length == 2)
                {
                    Check(codes.value[0] == "soil-medium-normal", $"material 0 \"{codes.value[0]}\"", ref failures);
                    Check(codes.value[1] == "grass-medium-normal", $"material 1 \"{codes.value[1]}\"", ref failures);
                    // The codes must resolve in the target registry.
                    Check(ResolveByCode(local, codes.value[0]) >= 0, "material 0 not found in the local registry", ref failures);
                    Check(ResolveByCode(local, codes.value[1]) >= 0, "material 1 not found in the local registry", ref failures);
                }
            }
            else
            {
                Console.WriteLine("   FAIL: the materials are still ids, not codes");
                failures++;
            }

            // Decor: server id 3 (grass) → local id 4.
            if (tree["decorIds"] is IntArrayAttribute decors)
            {
                Check(decors.value.Length == 1 && decors.value[0] == 4,
                    $"decorIds = [{string.Join(",", decors.value)}], expected [4]", ref failures);
            }
            else
            {
                Console.WriteLine("   FAIL: decorIds are gone");
                failures++;
            }

            Check(tree.GetInt("posx") == 2 && tree.GetInt("posy") == 67 && tree.GetInt("posz") == 3,
                "the block entity coordinates survived", ref failures);
            Check(tree.GetString("blockCode") == "chiseledblock", "the host block code survived", ref failures);
        }

        _ = withBe;
        return failures;
    }

    /// <summary>
    /// Compare block codes before and after translation: the palette of the reference
    /// capture against the palette of the built world. The codes must match entry by entry.
    /// </summary>
    private static int VerifyCodesUnchanged(string captureDir, string worldPath, BlockRegistryTable local)
    {
        int failures = 0;

        // The server registry codes come from the capture.
        var server = CaptureModel.Load(captureDir, null).BlockRegistry();
        if (server.Count == 0) { Console.WriteLine("   FAIL: the capture has no block registry"); return 1; }

        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(worldPath, ref error, false, false, false))
        {
            Console.WriteLine("   FAIL: the world did not open: " + error);
            return 1;
        }

        var serverCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string code in server.Values) serverCodes.Add(BlockRegistryTable.NormalizeCode(code));

        var positions = Enumerable.Range(0, 8).Select(cy => new ChunkPos(0, cy, 0, 0)).ToList();
        var blobs = db.GetChunks(positions).ToList();
        var codec = new GameZstdCodec();

        int checkedLayers = 0;
        foreach (byte[] blob in blobs)
        {
            var raw = SerializerUtil.Deserialize<ServerChunk>(blob);
            if (raw.blocksCompressed == null) continue;
            if (!CombinedLayerBlob.TryReadPalette(raw.blocksCompressed, codec, out int[]? palette, out _, out _)) continue;

            foreach (int id in palette!)
            {
                if (!local.Blocks.TryGetValue(id, out string? localCode))
                {
                    Console.WriteLine($"   FAIL: the world has id {id}, which is not in the local registry");
                    failures++;
                    continue;
                }
                // The code of a local block must also exist on the server.
                if (!serverCodes.Contains(BlockRegistryTable.NormalizeCode(localCode)))
                {
                    Console.WriteLine($"   FAIL: id {id} — \"{localCode}\", no such code in the server registry");
                    failures++;
                }
            }
            checkedLayers++;
        }

        Console.WriteLine(failures == 0
            ? $"   OK: the palettes of {checkedLayers} layers reference only codes known to the server"
            : $"   FAIL: {failures} mismatches");
        return failures;
    }

    private static void CreateTemplate(string path, int mapSizeY)
    {
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(path, ref error, true, false, false))
            throw new InvalidOperationException("Could not create the template: " + error);

        var save = new SaveGame
        {
            MapSizeX = 256,
            MapSizeY = mapSizeY,
            MapSizeZ = 256,
            Seed = 12345,
            WorldName = "selftest",
            CreatedGameVersion = "1.22.7",
            LastSavedGameVersion = "1.22.7",
            ModData = new ConcurrentDictionary<string, byte[]>(4, 16)
        };
        db.StoreGameData(SerializerUtil.Serialize(save));
    }

    private static void WriteSyntheticCapture(string dir, StandaloneChunkDataPool pool, int[] expected, string[] blockCodes)
    {
        var manifest = new CaptureManifest { GameVersion = "1.22.7", ServerName = "selftest" };
        using var writer = new CaptureWriter(dir, manifest);

        // Server identification.
        var identification = new Packet_ServerIdentification
        {
            GameVersion = "1.22.7",
            ServerName = "selftest",
            MapSizeX = 256,
            MapSizeY = 256,
            MapSizeZ = 256,
            Seed = 12345
        };
        writer.Write(CaptureRecordType.ServerIdentification,
            CapturePayloadCodec.Encode(PacketMapping.ToPayload(identification)));

        // Block registry.
        var assets = new Packet_ServerAssets();
        var blockTypes = new Packet_BlockType[blockCodes.Length];
        for (int i = 0; i < blockCodes.Length; i++)
        {
            blockTypes[i] = new Packet_BlockType { BlockId = i, Code = blockCodes[i] };
        }
        assets.SetBlocks(blockTypes);
        writer.Write(CaptureRecordType.ServerAssets, CapturePayloadCodec.Encode(PacketMapping.ToPayload(assets)));

        // World metadata with the configuration: gamedata is assembled from it
        // when a save is created without a template.
        var worldConfig = new Vintagestory.API.Datastructures.TreeAttribute();
        worldConfig.SetString("worldHeight", "256");
        worldConfig.SetString("playstyle", "surviveandbuild");
        var worldMeta = new Packet_WorldMetaData
        {
            SeaLevel = 110,
            SunBrightness = 20,
            WorldConfiguration = worldConfig.ToBytes()
        };
        writer.Write(CaptureRecordType.WorldMetaData, CapturePayloadCodec.Encode(PacketMapping.ToPayload(worldMeta)));

        // Heightmap of column (0,0).
        var heights = new ushort[1024];
        for (int i = 0; i < heights.Length; i++) heights[i] = 70;
        var mapChunk = new Packet_ServerMapChunk
        {
            ChunkX = 0,
            ChunkZ = 0,
            Ymax = 90,
            RainHeightMap = ArrayConvert.UshortToByte(heights),
            TerrainHeightMap = ArrayConvert.UshortToByte(heights)
        };
        writer.Write(CaptureRecordType.MapChunk, CapturePayloadCodec.Encode(PacketMapping.ToPayload(mapChunk)));

        // Eight sections of a single column — real packets assembled by the game.
        for (int cy = 0; cy < 8; cy++)
        {
            var chunk = ServerChunk.CreateNew(pool);
            var data = chunk.Data;
            int baseY = cy * ChunkSize;
            for (int y = 0; y < ChunkSize; y++)
            for (int z = 0; z < ChunkSize; z++)
            for (int x = 0; x < ChunkSize; x++)
            {
                int wy = baseY + y;
                data[Index(x, y, z)] = wy switch
                {
                    < 68 => 1,
                    68 => 2,
                    69 => 3,
                    _ => 0
                };
            }

            var packet = ToPacket(chunk, 0, cy, 0);

            // Put a chiseled block entity into section 2 — that exercises the whole
            // path: packet → capture → world build → translation inside the data.
            if (cy == 2) packet.SetBlockEntities([MakeSyntheticChisel(cy)]);

            writer.Write(CaptureRecordType.Chunk, CapturePayloadCodec.Encode(PacketMapping.ToPayload(packet)));
        }
    }

    /// <summary>
    /// Synthetic chiseled block entity: the materials are written as server block ids
    /// (2 = soil, 3 = grass), and decor is ids as well. That is exactly what the server sends.
    /// </summary>
    private static Packet_BlockEntity MakeSyntheticChisel(int cy)
    {
        var tree = new TreeAttribute();
        tree.SetInt("posx", 2);
        tree.SetInt("posy", cy * ChunkSize + 3);
        tree.SetInt("posz", 3);
        tree.SetString("blockCode", "chiseledblock");
        tree["materials"] = new IntArrayAttribute([2, 3]);
        tree["decorIds"] = new IntArrayAttribute([3]);
        tree["cuboids"] = new IntArrayAttribute([0x00FFFFFF]);

        return new Packet_BlockEntity
        {
            Classname = "MicroBlock",
            PosX = 2,
            PosY = cy * ChunkSize + 3,
            PosZ = 3,
            Data = tree.ToBytes()
        };
    }

    private static int VerifyOutput(string path, int[] expected, string[] blockCodes)
    {
        int failures = 0;
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(path, ref error, true, false, false))
        {
            Console.WriteLine("   FAIL: the game class could not open the result: " + error);
            return 1;
        }

        // gamedata must contain BlockIDs.
        byte[] rawGameData = db.GetGameData();
        if (rawGameData == null) { Console.WriteLine("   FAIL: no gamedata"); return 1; }
        var save = SerializerUtil.Deserialize<SaveGame>(rawGameData);
        if (save.ModData == null || !save.ModData.TryGetValue("BlockIDs", out byte[] blockIdsRaw))
        {
            Console.WriteLine("   FAIL: BlockIDs was not found in gamedata.ModData");
            failures++;
        }
        else
        {
            var registry = SerializerUtil.Deserialize<Dictionary<int, string>>(blockIdsRaw);
            Console.WriteLine($"   BlockIDs: {registry.Count} mappings");
            for (int i = 0; i < blockCodes.Length; i++)
            {
                if (!registry.TryGetValue(i, out string code) || code != blockCodes[i])
                {
                    Console.WriteLine($"   FAIL: BlockIDs[{i}] = \"{code}\", expected \"{blockCodes[i]}\"");
                    failures++;
                }
            }
        }

        // All 8 sections must read back and match the reference.
        var positions = Enumerable.Range(0, 8).Select(cy => new ChunkPos(0, cy, 0, 0)).ToList();
        var blobs = db.GetChunks(positions).ToList();
        Console.WriteLine($"   GetChunks returned {blobs.Count} of 8 sections");
        if (blobs.Count != 8) { Console.WriteLine("   FAIL: not all sections of the column were read"); failures++; }

        int checkedSections = 0;
        for (int section = 0; section < blobs.Count; section++)
        {
            int[] actual = ReadBlocksWorldFree(blobs[section]);

            // The blocks of each section must match the reference pattern.
            for (int i = 0; i < actual.Length; i++)
            {
                int expect = expected[section * BlocksPerChunk + i];
                if (actual[i] == expect) continue;
                if (failures < 10)
                {
                    int x = i & 31, z = (i >> 5) & 31, y = (i >> 10) & 31;
                    Console.WriteLine($"   FAIL: section {section}, (x={x},y={y},z={z}): expected {expect}, got {actual[i]}");
                }
                failures++;
            }
            checkedSections++;
        }

        Console.WriteLine($"   Sections checked: {checkedSections}");
        return failures;
    }

    /// <summary>
    /// Read the blocks of a chunk WITHOUT a world: ServerChunk.FromBytes triggers
    /// AfterDeserialization for block entities, which needs a real world (it resolves
    /// the block entity's host block). So we parse the protobuf directly and decode the
    /// blobs the client way — it needs no world.
    /// </summary>
    private static int[] ReadBlocksWorldFree(byte[] blob)
    {
        var raw = SerializerUtil.Deserialize<ServerChunk>(blob);
        var chunk = Vintagestory.Client.NoObf.ClientChunk.CreateNewCompressed(
            new StandaloneChunkDataPool(), raw.blocksCompressed, raw.lightCompressed,
            raw.lightSatCompressed, raw.fluidsCompressed,
            SerializerUtil.Serialize(new Dictionary<string, byte[]>()), raw.savedCompressionVersion);
        chunk.Unpack_ReadOnly();

        var data = chunk.Data;
        var result = new int[data.Length];
        data.TakeBulkReadLock();
        try
        {
            for (int i = 0; i < result.Length; i++)
                result[i] = data.GetBlockId(i, Vintagestory.API.Common.BlockLayersAccess.SolidBlocks);
        }
        finally
        {
            data.ReleaseBulkReadLock();
        }
        return result;
    }

    /// <summary>
    /// Check a save created without a template: gamedata must parse with the game's
    /// SaveGame, contain the world configuration and the block registry, and the chunks
    /// must read back.
    /// </summary>
    private static int VerifyFreshSave(string path, int[] expected, bool expectBlockIds)
    {
        int failures = 0;
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(path, ref error, false, false, false))
        {
            Console.WriteLine("   FAIL: the save did not open: " + error);
            return 1;
        }

        byte[] raw = db.GetGameData();
        if (raw == null) { Console.WriteLine("   FAIL: no gamedata"); return 1; }

        SaveGame save = SerializerUtil.Deserialize<SaveGame>(raw);

        // The game parses WorldConfigBytes into WorldConfiguration with exactly this call
        // (SaveGame.LoadWorldConfig), so the check must do the same.
        save.LoadWorldConfig();

        Console.WriteLine($"   gamedata: MapSize {save.MapSizeX}x{save.MapSizeY}x{save.MapSizeZ}, "
                          + $"seed {save.Seed}, name \"{save.WorldName}\"");

        Check(save.MapSizeY == 256, "world height from the capture", ref failures);
        Check(save.Seed == 12345, "seed from the capture", ref failures);
        Check(save.WorldName == "selftest-direct", "world name", ref failures);
        Check(save.DefaultSpawn != null, "spawn point is set", ref failures);

        // Fields the game sets in its own SaveGame.CreateNew. A zero here makes the
        // game apply the legacy planter remap again.
        // The field is marked [Obsolete], but the game still writes and reads it.
#pragma warning disable CS0612
        Check(save.LastBlockItemMappingVersion >= 1,
            $"LastBlockItemMappingVersion = {save.LastBlockItemMappingVersion} (must be >= 1)", ref failures);
#pragma warning restore CS0612
        Check(save.RemappingsAppliedByCode != null && save.RemappingsAppliedByCode.ContainsKey("game:v1.12clayplanters"),
            "the 1.12 planter remap flag is set", ref failures);

        // The world configuration is the main thing a correct new world needs.
        Check(save.WorldConfiguration != null, "the world configuration parsed", ref failures);
        if (save.WorldConfiguration != null)
        {
            Check(save.WorldConfiguration.GetString("worldHeight") == "256",
                "worldHeight carried over from the capture", ref failures);
        }

        bool hasBlockIds = save.ModData != null && save.ModData.ContainsKey("BlockIDs");
        Check(hasBlockIds == expectBlockIds,
            $"BlockIDs {(expectBlockIds ? "was not written" : "was written although it should not be")}", ref failures);

        int chunkRows = 0;
        using (var conn = OpenSqlite(path))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunk";
            chunkRows = Convert.ToInt32(cmd.ExecuteScalar());
        }
        Check(chunkRows == 8, $"chunk rows = {chunkRows} (expected 8)", ref failures);

        return failures;
    }

    private static void Check(bool condition, string what, ref int failures)
    {
        if (condition) return;
        Console.WriteLine("   FAIL: " + what);
        failures++;
    }

    /// <summary>
    /// Read the chunks from the world and compare the blocks with the reference. Used to
    /// check id translation: the world must already hold local ids.
    /// </summary>
    private static int VerifyOutputBlocks(string path, int[] expected, string what)
    {
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;
        string error = null;
        if (!connection.OpenOrCreate(path, ref error, false, false, false))
        {
            Console.WriteLine("   FAIL: the world did not open: " + error);
            return 1;
        }

        var positions = Enumerable.Range(0, 8).Select(cy => new ChunkPos(0, cy, 0, 0)).ToList();
        var blobs = db.GetChunks(positions).ToList();
        if (blobs.Count != 8)
        {
            Console.WriteLine($"   FAIL ({what}): read {blobs.Count} of 8 sections");
            return 1;
        }

        var pool = new StandaloneChunkDataPool();
        byte[] emptyModdata = SerializerUtil.Serialize(new Dictionary<string, byte[]>());
        int failures = 0;

        for (int section = 0; section < blobs.Count; section++)
        {
            var raw = SerializerUtil.Deserialize<ServerChunk>(blobs[section]);
            var chunk = Vintagestory.Client.NoObf.ClientChunk.CreateNewCompressed(
                pool, raw.blocksCompressed, raw.lightCompressed, raw.lightSatCompressed,
                raw.fluidsCompressed, emptyModdata, raw.savedCompressionVersion);
            chunk.Unpack_ReadOnly();

            var data = chunk.Data;
            data.TakeBulkReadLock();
            try
            {
                for (int i = 0; i < data.Length; i++)
                {
                    int actual = data.GetBlockId(i, Vintagestory.API.Common.BlockLayersAccess.SolidBlocks);
                    int want = expected[section * BlocksPerChunk + i];
                    if (actual == want) continue;
                    if (failures < 5)
                    {
                        int x = i & 31, z = (i >> 5) & 31, y = (i >> 10) & 31;
                        Console.WriteLine($"   FAIL ({what}): section {section}, (x={x},y={y},z={z}): expected {want}, got {actual}");
                    }
                    failures++;
                }
            }
            finally
            {
                data.ReleaseBulkReadLock();
            }
        }

        Console.WriteLine(failures == 0
            ? $"   OK ({what}): the blocks in the world have translated ids"
            : $"   FAIL ({what}): {failures} mismatches");
        return failures;
    }

    private static int[] BuildPattern()
    {
        var expected = new int[BlocksPerChunk * 8];
        for (int cy = 0; cy < 8; cy++)
        {
            int baseY = cy * ChunkSize;
            for (int y = 0; y < ChunkSize; y++)
            for (int z = 0; z < ChunkSize; z++)
            for (int x = 0; x < ChunkSize; x++)
            {
                int wy = baseY + y;
                expected[cy * BlocksPerChunk + Index(x, y, z)] = wy switch
                {
                    < 68 => 1,
                    68 => 2,
                    69 => 3,
                    _ => 0
                };
            }
        }
        return expected;
    }

    private static int Index(int x, int y, int z) => x | (z << 5) | (y << 10);

    /// <summary>A chiseled block is the MicroBlock class (see BlockEntityRewrite).</summary>
    private static bool IsMicroBlock(string? classname)
        => classname != null && classname.Contains("MicroBlock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Uniform sampling over the whole list. Take(n) only takes the beginning, so empty
    /// first chunks would give a false picture of the whole world.
    /// </summary>
    private static List<T> SpreadSample<T>(IReadOnlyList<T> items, int count)
    {
        if (count <= 0 || items.Count == 0) return [];
        if (items.Count <= count) return items.ToList();

        var result = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            int index = (int)((long)i * items.Count / count);
            result.Add(items[index]);
        }
        return result;
    }

    /// <summary>internal ServerChunk.ToPacket(...) — the same code that builds packets on the server.</summary>
    private static Packet_ServerChunk ToPacket(ServerChunk chunk, int x, int y, int z)
    {
        var method = typeof(ServerChunk).GetMethod("ToPacket", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException("ServerChunk.ToPacket not found");
        return (Packet_ServerChunk)method.Invoke(chunk, [x, y, z, false]);
    }

    private static Assembly Resolve(AssemblyLoadContext ctx, AssemblyName name)
    {
        foreach (string candidate in new[]
                 {
                     Path.Combine(GameDir, name.Name + ".dll"),
                     Path.Combine(GameDir, "Lib", name.Name + ".dll")
                 })
        {
            if (File.Exists(candidate)) return ctx.LoadFromAssemblyPath(candidate);
        }
        return null;
    }
}
