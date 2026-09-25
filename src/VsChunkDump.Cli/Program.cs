using System.Diagnostics;
using VsChunkDump.Core;

namespace VsChunkDump.Cli;

/// <summary>
/// Console viewer for chunk dumps: builds PNG previews
/// (top view, isometric, horizontal slice) and prints statistics
/// without launching the game.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            string command = args[0];
            var a = ArgReader.Parse(args.AsSpan(1));
            return command switch
            {
                "inspect" => Inspect(a),
                "blocks" => Blocks(a),
                "verify" => Verify(a),
                "demo" => Demo(a),
                "render-top" => RenderTop(a),
                "render-iso" => RenderIso(a),
                "render-slice" => RenderSlice(a),
                _ => Fail($"Unknown command: {command}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            if (Environment.GetEnvironmentVariable("VSCHUNKDUMP_DEBUG") == "1")
                Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            vschunkdump — viewer for Vintage Story chunk dumps in the vschunkdump format.

            Usage:
              vschunkdump <command> <dump-directory> [options]

            Commands:
              inspect        dump summary: dimensions, bounds, chunk count, heights
              blocks         block table and histogram (top-N)
              verify         verify the integrity of all records (CRC + decompression)
              demo           generate a synthetic dump (to test without the game)
              render-top     PNG top-down map (color of the top block)
              render-iso     PNG isometric view of the surface
              render-slice   PNG slice at the given Y height

            Common options:
              --dim <n>            dimension (default 0)
              --area x0,z0,x1,z1   restrict the area, in blocks
              -o, --out <file>     path to the PNG (required for render-*)

            Render options:
              --scale <n>          pixels per block for top/slice (default 2)
              --grid               draw the chunk grid (32 blocks) on the top view
              --tile <n>           diamond width in pixels for the isometric view (default 6)
              --yscale <n>         pixels per height block in the isometric view (default 3)
              --y <n>              slice height for render-slice

            blocks options:
              --top <n>            how many histogram rows to print (default 25)

            Examples:
              vschunkdump inspect ./dumps/myserver
              vschunkdump render-top ./dumps/myserver -o map.png --scale 3 --grid
              vschunkdump render-iso ./dumps/myserver -o base.png --tile 8
              vschunkdump render-slice ./dumps/myserver -o floor.png --y 72
            """);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Inspect(ArgReader a)
    {
        var set = DumpSet.Open(a.Positional(0));
        var m = set.Manifest;

        Console.WriteLine($"Dump:              {Path.GetFullPath(set.Root)}");
        Console.WriteLine($"Format:            {m.Format} v{m.FormatVersion}");
        Console.WriteLine($"Game version:      {m.GameVersion ?? "unknown"}");
        Console.WriteLine($"Mod version:       {m.ModVersion ?? "unknown"}");
        Console.WriteLine($"World:             {m.WorldName ?? "unknown"}");
        Console.WriteLine($"Server:            {m.ServerAddress ?? "—"}");
        Console.WriteLine($"Savegame ID:       {m.SavegameIdentifier ?? "—"}");
        Console.WriteLine($"Seed:              {m.Seed}");
        Console.WriteLine($"Chunk size:        {m.ChunkSize}^3 = {m.ChunkSize * m.ChunkSize * m.ChunkSize} blocks");
        Console.WriteLine($"World height:      {m.MapSizeY} ({m.SectionCountY} sections)");
        Console.WriteLine($"Blocks in palette: {m.BlockCodes.Count}");
        Console.WriteLine($"Created:           {m.CreatedUtc:u}");
        Console.WriteLine($"Updated:           {m.UpdatedUtc:u}");
        Console.WriteLine($"Total records:     {m.RecordsWritten}");
        Console.WriteLine();

        int dim = a.Int("--dim", 0);
        long count = 0;
        int minCx = int.MaxValue, maxCx = int.MinValue, minCz = int.MaxValue, maxCz = int.MinValue;
        int minCy = int.MaxValue, maxCy = int.MinValue;
        var perSection = new SortedDictionary<int, long>();

        foreach (var (x, y, z) in set.ChunkCoords(dim))
        {
            count++;
            minCx = Math.Min(minCx, x); maxCx = Math.Max(maxCx, x);
            minCz = Math.Min(minCz, z); maxCz = Math.Max(maxCz, z);
            minCy = Math.Min(minCy, y); maxCy = Math.Max(maxCy, y);
            perSection[y] = perSection.GetValueOrDefault(y) + 1;
        }

        if (count == 0)
        {
            Console.WriteLine($"No chunks in dimension {dim}.");
            Console.WriteLine("Available dimensions: " + string.Join(", ", set.Dimensions()));
            return 1;
        }

        int s = ChunkGeometry.ChunkSize;
        Console.WriteLine($"Dimension {dim}: {count} chunks (records, including repeats)");
        Console.WriteLine($"  Chunk range X: {minCx}..{maxCx}   Z: {minCz}..{maxCz}   Y sections: {minCy}..{maxCy}");
        Console.WriteLine($"  Area:             {(maxCx - minCx + 1)} x {(maxCz - minCz + 1)} chunks = "
                          + $"{(maxCx - minCx + 1) * s} x {(maxCz - minCz + 1) * s} blocks");
        Console.WriteLine($"  World bounds:     X {minCx * s}..{(maxCx + 1) * s - 1}  "
                          + $"Y {minCy * s}..{(maxCy + 1) * s - 1}  "
                          + $"Z {minCz * s}..{(maxCz + 1) * s - 1}");
        Console.WriteLine("  Chunks per Y section: " + string.Join(", ", perSection.Select(kv => $"y{kv.Key}={kv.Value}")));
        Console.WriteLine();
        Console.WriteLine("Size on disk:");
        foreach (string dir in Directory.EnumerateDirectories(set.Root, "dim*").OrderBy(d => d))
        {
            long bytes = Directory.EnumerateFiles(dir, "*.vscr").Sum(f => new FileInfo(f).Length);
            Console.WriteLine($"  {Path.GetFileName(dir)}: {FormatBytes(bytes)}");
        }
        return 0;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GiB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:F2} MiB",
        >= 1024 => $"{bytes / 1024.0:F1} KiB",
        _ => $"{bytes} B"
    };

    private static int Blocks(ArgReader a)
    {
        var set = DumpSet.Open(a.Positional(0));
        int dim = a.Int("--dim", 0);
        int top = a.Int("--top", 25);

        var surface = SurfaceModel.Build(set, dim);
        Console.WriteLine($"Total blocks in dump: {surface.TotalBlocks:N0} ({surface.ChunksRead} chunks)");
        Console.WriteLine($"Unique block ids: {surface.Histogram.Count}");
        Console.WriteLine();
        Console.WriteLine($"{"share",8}  {"count",14}  id    block code");
        foreach (var kv in surface.Histogram.OrderByDescending(k => k.Value).Take(top))
        {
            double pct = 100.0 * kv.Value / Math.Max(1, surface.TotalBlocks);
            Console.WriteLine($"{pct,7:F2}%  {kv.Value,14:N0}  {kv.Key,-5} {set.Blocks.Code(kv.Key)}");
        }
        return 0;
    }

    private static int Verify(ArgReader a)
    {
        var set = DumpSet.Open(a.Positional(0));
        int dim = a.Int("--dim", 0);
        long ok = 0, bad = 0;
        var sw = Stopwatch.StartNew();
        foreach (var chunk in set.ReadChunks(dim))
        {
            if (chunk.Blocks.Length != ChunkGeometry.BlocksPerChunk) { bad++; continue; }
            if (chunk.Fluids != null && chunk.Fluids.Length != ChunkGeometry.BlocksPerChunk) { bad++; continue; }
            ok++;
        }
        sw.Stop();
        Console.WriteLine($"Records checked: {ok + bad} (ok: {ok}, bad: {bad}) in {sw.ElapsedMilliseconds} ms");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// Generate a synthetic dump to test rendering without launching the game.
    /// World: rolling terrain, a pond, a log house, and a stone tower.
    /// </summary>
    private static int Demo(ArgReader a)
    {
        string outDir = a.Positional(0);
        if (string.IsNullOrWhiteSpace(outDir))
            return Fail("Specify a directory: vschunkdump demo <directory> [--columns N] [--ground Y]");

        int columns = Math.Clamp(a.Int("--columns", 3), 1, 32);
        int ground = a.Int("--ground", 70);

        string[] codes =
        [
            "game:air",
            "game:rock-granite",
            "game:soil-medium-normal",
            "game:grass-medium-normal",
            "game:water-still-7",
            "game:planks-oak",
            "game:log-oak-grown",
            "game:cobblestone-granite",
            "game:glass",
            "game:sand-yellow"
        ];
        const int Air = 0, Rock = 1, Soil = 2, Grass = 3, Water = 4, Planks = 5, Log = 6, Cobble = 7, Glass = 8, Sand = 9;

        var manifest = new DumpManifest
        {
            GameVersion = "demo",
            ModVersion = "0.0.0",
            WorldName = "vschunkdump demo",
            Seed = 12345,
            BlockCodes = [.. codes]
        };

        int size = columns * ChunkGeometry.ChunkSize;
        // Terrain height: two sine waves plus a pond in the lowland.
        int GroundAt(int x, int z) => ground + (int)Math.Round(3 * Math.Sin(x / 9.0) + 2 * Math.Cos(z / 7.0));

        // Sections with headroom above: 20 blocks over the ground plus the section containing the surface itself.
        int sections = (ground + 20) / ChunkGeometry.ChunkSize + 1;

        using (var writer = new DumpWriter(outDir, manifest))
        {
            for (int cz = 0; cz < columns; cz++)
            for (int cx = 0; cx < columns; cx++)
            for (int cy = 0; cy < sections; cy++)
            {
                var blocks = new int[ChunkGeometry.BlocksPerChunk];
                int baseY = cy * ChunkGeometry.ChunkSize;

                for (int lz = 0; lz < ChunkGeometry.ChunkSize; lz++)
                for (int lx = 0; lx < ChunkGeometry.ChunkSize; lx++)
                {
                    int wx = cx * ChunkGeometry.ChunkSize + lx;
                    int wz = cz * ChunkGeometry.ChunkSize + lz;
                    int top = GroundAt(wx, wz);

                    for (int ly = 0; ly < ChunkGeometry.ChunkSize; ly++)
                    {
                        int wy = baseY + ly;
                        int id = Air;
                        if (wy < top - 2) id = Rock;
                        else if (wy < top) id = Soil;
                        else if (wy == top) id = Grass;

                        // Pond: lower the terrain and fill it with water.
                        double pd = Math.Sqrt((wx - size * 0.72) * (wx - size * 0.72) + (wz - size * 0.3) * (wz - size * 0.3));
                        if (pd < 9)
                        {
                            if (wy <= top - 2 && wy > top - 6) id = Water;
                            else if (wy > top - 2 && wy <= top) id = Air;
                        }

                        blocks[ChunkGeometry.Index3d(lx, ly, lz)] = id;
                    }

                    // 12x12 log house with a plank roof.
                    int hx = size / 2 - 6, hz = size / 2 - 6;
                    if (wx >= hx && wx < hx + 12 && wz >= hz && wz < hz + 12)
                    {
                        bool wall = wx == hx || wx == hx + 11 || wz == hz || wz == hz + 11;
                        for (int ly = 0; ly < ChunkGeometry.ChunkSize; ly++)
                        {
                            int wy = baseY + ly;
                            int rel = wy - GroundAt(wx, wz);
                            bool inChunk = wx >= cx * 32 && wx < (cx + 1) * 32 && wz >= cz * 32 && wz < (cz + 1) * 32;
                            if (!inChunk) continue;
                            int index = ChunkGeometry.Index3d(lx, ly, lz);
                            if (rel >= 1 && rel <= 4 && wall) blocks[index] = Log;
                            else if (rel == 5) blocks[index] = Planks;
                            else if (rel >= 2 && rel <= 4 && (wx == hx + 5 && (wz == hz || wz == hz + 11)))
                                blocks[index] = Glass;
                        }
                    }

                    // 7x7 cobblestone tower with a stepped top.
                    int tx = size / 4, tz = size / 4;
                    if (wx >= tx && wx < tx + 7 && wz >= tz && wz < tz + 7)
                    {
                        bool ring = wx == tx || wx == tx + 6 || wz == tz || wz == tz + 6;
                        bool inChunk = wx >= cx * 32 && wx < (cx + 1) * 32 && wz >= cz * 32 && wz < (cz + 1) * 32;
                        if (ring && inChunk)
                        {
                            for (int ly = 0; ly < ChunkGeometry.ChunkSize; ly++)
                            {
                                int wy = baseY + ly;
                                int rel = wy - GroundAt(wx, wz);
                                if (rel >= 1 && rel <= 14)
                                {
                                    blocks[ChunkGeometry.Index3d(lx, ly, lz)] = (rel == 14) ? Sand : Cobble;
                                }
                            }
                        }
                    }
                }

                writer.TryWriteChunk(new ChunkRecord { Dim = 0, X = cx, Y = cy, Z = cz, Blocks = blocks });
            }
            writer.Flush();
        }

        var opened = DumpSet.Open(outDir);
        Console.WriteLine($"Demo dump created: {Path.GetFullPath(outDir)}");
        Console.WriteLine($"  {columns}x{columns} chunks ({size}x{size} blocks), palette of {codes.Length} blocks");
        Console.WriteLine($"  unique chunks: {opened.Manifest.UniqueChunks}");
        Console.WriteLine();
        Console.WriteLine("Next you can try:");
        Console.WriteLine($"  vschunkdump inspect {outDir}");
        Console.WriteLine($"  vschunkdump render-top {outDir} -o demo-top.png --scale 2 --grid");
        Console.WriteLine($"  vschunkdump render-iso {outDir} -o demo-iso.png --tile 6 --yscale 3");
        Console.WriteLine($"  vschunkdump render-slice {outDir} -o demo-slice.png --y {ground + 2}");
        return 0;
    }

    private static int RenderTop(ArgReader a)
    {
        var set = DumpSet.Open(a.Positional(0));
        string outPath = a.Require("--out", "-o");
        int dim = a.Int("--dim", 0);
        int scale = a.Int("--scale", 2);
        bool grid = a.Has("--grid");

        var surface = SurfaceModel.Build(set, dim, a.Area());
        WarnIfHuge(surface.Width * scale, surface.Depth * scale);

        var img = TopDownRenderer.Render(surface, scale, shadeByHeight: true, chunkGrid: grid, table: set.Blocks);
        Save(img, outPath);
        PrintSurfaceInfo(surface, img, outPath);
        return 0;
    }

    private static int RenderIso(ArgReader a)
    {
        var set = DumpSet.Open(a.Positional(0));
        string outPath = a.Require("--out", "-o");
        int dim = a.Int("--dim", 0);
        int tile = a.Int("--tile", 6);
        int yScale = a.Int("--yscale", 3);

        var surface = SurfaceModel.Build(set, dim, a.Area());
        var img = IsometricRenderer.Render(surface, set.Blocks, tile, yScale);
        Save(img, outPath);
        Console.WriteLine($"Isometric: {img.Width}x{img.Height} px -> {outPath}");
        Console.WriteLine("Note: the view is built from the heightmap; overhangs are not shown.");
        return 0;
    }

    private static int RenderSlice(ArgReader a)
    {
        var set = DumpSet.Open(a.Positional(0));
        string outPath = a.Require("--out", "-o");
        int dim = a.Int("--dim", 0);
        int scale = a.Int("--scale", 2);
        if (!a.Has("--y")) return Fail("render-slice requires the --y <height in blocks> parameter");
        int y = a.Int("--y", 64);

        var area = a.Area() ?? DefaultBounds(set, dim);
        var img = SliceRenderer.Render(set, dim, y, area, set.Blocks, scale);
        Save(img, outPath);
        Console.WriteLine($"Slice Y={y}: {img.Width}x{img.Height} px, area X {area.MinX}..{area.MaxX}, Z {area.MinZ}..{area.MaxZ} -> {outPath}");
        return 0;
    }

    private static BlockBounds DefaultBounds(DumpSet set, int dim)
    {
        int minCx = int.MaxValue, maxCx = int.MinValue, minCz = int.MaxValue, maxCz = int.MinValue;
        bool any = false;
        foreach (var (x, _, z) in set.ChunkCoords(dim))
        {
            any = true;
            minCx = Math.Min(minCx, x); maxCx = Math.Max(maxCx, x);
            minCz = Math.Min(minCz, z); maxCz = Math.Max(maxCz, z);
        }
        if (!any) throw new InvalidOperationException($"The dump has no chunks in dimension {dim}");
        int s = ChunkGeometry.ChunkSize;
        return new BlockBounds(minCx * s, minCz * s, (maxCx + 1) * s - 1, (maxCz + 1) * s - 1);
    }

    private static void Save(RenderedImage img, string path)
    {
        PngEncoder.WriteRgba(path, img.Width, img.Height, img.Pixels);
    }

    private static void PrintSurfaceInfo(SurfaceModel s, RenderedImage img, string path)
    {
        var (minY, maxY) = s.HeightRange();
        Console.WriteLine($"Chunks read: {s.ChunksRead}, blocks: {s.TotalBlocks:N0}");
        Console.WriteLine($"Area: X {s.Bounds.MinX}..{s.Bounds.MaxX}, Z {s.Bounds.MinZ}..{s.Bounds.MaxZ}");
        Console.WriteLine($"Heights: {minY}..{maxY}");
        Console.WriteLine($"Map: {img.Width}x{img.Height} px -> {path}");
    }

    private static void WarnIfHuge(int w, int h)
    {
        long px = (long)w * h;
        if (px > 200_000_000)
            throw new InvalidOperationException($"Image is too large: {w}x{h}. Reduce --scale or set --area.");
        if (px > 40_000_000)
            Console.Error.WriteLine($"Warning: image {w}x{h} ({px * 4 / 1024 / 1024} MiB in memory); this may take a while.");
    }
}
