using System.Reflection;
using System.Runtime.Loader;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

namespace VsVcdbsProbe;

/// <summary>
/// Chunk data pool stub: <see cref="ChunkDataPool"/> has a protected constructor,
/// so a full <see cref="ServerChunk"/> can be built without a ServerMain instance.
/// </summary>
public sealed class StandaloneChunkDataPool : ChunkDataPool
{
    public StandaloneChunkDataPool(int chunkSize = 32)
    {
        chunksize = chunkSize;
        BlackHoleData = ChunkData.CreateNew(chunkSize, this);
        OnlyAirBlocksData = NoChunkData.CreateNew(chunkSize);
    }
}

internal static class Program
{
    private const int ChunkSize = 32;
    private const int BlocksPerChunk = ChunkSize * ChunkSize * ChunkSize;

    private static string GameDir =>
        Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "";

    /// <summary>The probe executes game code, so it needs an installed game.</summary>
    private static bool EnsureGameDir()
    {
        if (!string.IsNullOrWhiteSpace(GameDir)
            && File.Exists(Path.Combine(GameDir, "VintagestoryLib.dll")))
        {
            return true;
        }

        Console.Error.WriteLine(string.IsNullOrWhiteSpace(GameDir)
            ? "VINTAGE_STORY is not set — path to the Vintage Story installation directory."
            : $"There is no VintagestoryLib.dll in \"{GameDir}\" — check VINTAGE_STORY.");
        Console.Error.WriteLine("  export VINTAGE_STORY=/path/to/VintageStory");
        return false;
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception) { /* not critical */ }
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

        AssemblyLoadContext.Default.Resolving += Resolve;

        if (!EnsureGameDir()) return 2;

        // Inspect a real savegame.
        if (args.Length >= 1 && args[0] == "inspect")
        {
            return RealSaveInspect.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 200_000);
        }

        // Verify a mod archive: unpack and parse it with the game's real ModContainer.
        if (args.Length >= 1 && args[0] == "modzip")
        {
            return ModArchiveProbe.Run(args.Skip(1).ToArray());
        }

        Console.WriteLine("Vintage Story .vcdbs feasibility probe");
        Console.WriteLine("Game dir: " + GameDir);
        Console.WriteLine();

        int failures = 0;
        failures += Run("Hypothesis 1: server packet blobs == savegame blobs", ProbeWireEqualsSave);
        failures += Run("Hypothesis 2: ToBytes -> FromBytes (chunk table row)", ProbeSaveBlobRoundTrip);
        failures += Run("Hypothesis 3: writing and reading a real .vcdbs", ProbeVcdbsWriteRead);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "RESULT: all hypotheses confirmed — a full .vcdbs is feasible."
            : $"RESULT: {failures} failures.");
        return failures == 0 ? 0 : 1;
    }

    private static int Run(string title, Func<int> probe)
    {
        Console.WriteLine("--- " + title + " ---");
        try
        {
            return probe();
        }
        catch (Exception e)
        {
            Console.WriteLine("  FATAL: " + e);
            Console.WriteLine();
            return 1;
        }
    }

    /// <summary>
    /// Hypothesis 1: block/liquid/light blobs from a wire packet can be stored in a
    /// chunk table row WITHOUT re-encoding.
    ///
    /// We check exactly the algorithm the offline converter needs:
    ///   1. the game itself builds a real Packet_ServerChunk from a ServerChunk (ToPacket);
    ///   2. we create an empty ServerChunk and move it to the packed state through the
    ///      public path (Pack + TryCommitPackAndFree(0), which clears chunkdata and flags);
    ///   3. we substitute the blobs with the data from the packet;
    ///   4. ToBytes -> FromBytes and compare the blocks.
    /// </summary>
    private static int ProbeWireEqualsSave()
    {
        var pool = new StandaloneChunkDataPool();
        var source = BuildPatternChunk(pool);
        var packet = BuildPacketViaReflection(source, 0, 0, 0);

        Console.WriteLine($"  ToPacket -> blocks={Len(packet.Blocks)} liquids={Len(packet.Liquids)} " +
                          $"light={Len(packet.Light)} lightSat={Len(packet.LightSat)} compver={packet.Compver}");

        int failures = 0;
        if (packet.Blocks == null || packet.Blocks.Length == 0)
        {
            Console.WriteLine("  FAIL: the packet has no block blobs");
            return 1;
        }

        // --- offline assembly of a chunk table row from packet data (public API) ---
        var rebuilt = ServerChunk.CreateNew(pool);
        rebuilt.Pack();
        if (!rebuilt.TryCommitPackAndFree(0))
        {
            Console.WriteLine("  WARNING: TryCommitPackAndFree(0) returned false");
        }

        rebuilt.blocksCompressed = packet.Blocks;
        rebuilt.lightCompressed = packet.Light;
        rebuilt.lightSatCompressed = packet.LightSat;
        rebuilt.fluidsCompressed = packet.Liquids;

        byte[] blob = rebuilt.ToBytes();
        Console.WriteLine($"  ToBytes  -> {blob.Length} bytes (chunk table row, assembled from the packet)");

        // The blobs must land in the savegame byte-for-byte, with no repacking.
        var roundTripped = ServerChunk.FromBytes(blob, new StandaloneChunkDataPool(), null);
        bool sameBlocks = SequenceEqual(packet.Blocks, roundTripped.blocksCompressed);
        bool sameLiquids = SequenceEqual(packet.Liquids, roundTripped.fluidsCompressed);
        Console.WriteLine(sameBlocks && sameLiquids
            ? "  OK: the packet blobs are stored in the .vcdbs byte-for-byte (no re-encoding)"
            : $"  FAIL: blocks={sameBlocks} liquids={sameLiquids}");
        if (!(sameBlocks && sameLiquids)) failures++;

        int mismatches = Compare(ExpectedPattern(), ReadBlocks(roundTripped));
        Console.WriteLine(mismatches == 0
            ? "  OK: the blocks from the packet are read back correctly from the savegame format"
            : $"  FAIL: {mismatches} mismatches");
        if (mismatches != 0) failures++;

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// Hypothesis 2: ServerChunk.ToBytes -> ServerChunk.FromBytes preserves the blocks.
    /// This is exactly writing and reading a chunk table row.
    /// </summary>
    private static int ProbeSaveBlobRoundTrip()
    {
        var pool = new StandaloneChunkDataPool();
        var chunk = BuildPatternChunk(pool);
        byte[] blob = chunk.ToBytes();

        var restored = ServerChunk.FromBytes(blob, new StandaloneChunkDataPool(), null);
        int mismatches = Compare(ExpectedPattern(), ReadBlocks(restored));

        Console.WriteLine($"  blob={blob.Length} bytes, GameVersionCreated={restored.GameVersionCreated}, " +
                          $"savedCompressionVersion={restored.savedCompressionVersion}");
        Console.WriteLine(mismatches == 0
            ? "  OK: the blocks survived the round-trip through the savegame format"
            : $"  FAIL: {mismatches} mismatches");
        Console.WriteLine();
        return mismatches == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Hypothesis 3: a chunk table row can be written to a real .vcdbs and read back.
    /// </summary>
    private static int ProbeVcdbsWriteRead()
    {
        string path = Path.Combine(Path.GetTempPath(), "vcdbs-probe-" + Guid.NewGuid().ToString("N") + ".vcdbs");
        var pool = new StandaloneChunkDataPool();

        var written = new List<ProbeChunk>();
        // Real savegames contain only non-negative coordinates: the world is bounded
        // by MapSizeX/Z and chunks live in 0..MapSize/32-1 (verified on a real
        // .vcdbs, see the report).
        foreach (var (cx, cy, cz) in new[] { (0, 0, 0), (0, 1, 0), (1, 0, 2), (15982, 0, 15980), (3999, 2, 5000) })
        {
            var chunk = BuildPatternChunk(pool);
            written.Add(new ProbeChunk(cx, cy, cz, chunk.ToBytes()));
        }

        VcdbsProbe.Write(path, written);
        var read = VcdbsProbe.Read(path);
        Console.WriteLine($"  wrote {written.Count} rows, read {read.Count}, file {new FileInfo(path).Length} bytes");

        int failures = 0;
        foreach (var want in written)
        {
            var match = read.FirstOrDefault(r => r.X == want.X && r.Y == want.Y && r.Z == want.Z);
            if (match?.Blob == null)
            {
                Console.WriteLine($"  FAIL: chunk {want.X},{want.Y},{want.Z} not found in the database");
                failures++;
                continue;
            }

            var restored = ServerChunk.FromBytes(match.Blob, new StandaloneChunkDataPool(), null);
            int mismatches = Compare(ExpectedPattern(), ReadBlocks(restored));
            if (mismatches != 0)
            {
                Console.WriteLine($"  FAIL: chunk {want.X},{want.Y},{want.Z} — {mismatches} mismatches");
                failures++;
            }
        }

        // The ChunkPos key must encode and decode losslessly.
        foreach (var want in written)
        {
            ulong index = Vintagestory.Common.Database.ChunkPos.ToChunkIndex(want.X, want.Y, want.Z, 0);
            var back = Vintagestory.Common.Database.ChunkPos.FromChunkIndex_saveGamev2(index);
            if (back.X != want.X || back.Y != want.Y || back.Z != want.Z || back.Dimension != 0)
            {
                Console.WriteLine($"  FAIL: chunk key {want.X},{want.Y},{want.Z} -> {back.X},{back.Y},{back.Z}");
                failures++;
            }
        }

        // Schema limitation: 22 bits for X and Z, 9 bits for Y. Negative coordinates
        // cannot be represented — and real worlds never have them (the world is
        // bounded by MapSize).
        var negative = Vintagestory.Common.Database.ChunkPos.FromChunkIndex_saveGamev2(
            Vintagestory.Common.Database.ChunkPos.ToChunkIndex(-1, 0, 5, 0));
        Console.WriteLine($"  (for reference) negative X cannot be represented: -1,0,5 -> {negative.X},{negative.Y},{negative.Z}");

        Console.WriteLine(failures == 0
            ? "  OK: chunks were written to the .vcdbs, read back, and the ChunkPos keys are correct"
            : "  FAIL: reading from the .vcdbs produced mismatches");

        // Close the loop: the same file is read by the game's own class.
        failures += VcdbsProbe.GameReaderRoundTrip(path, written);

        try { File.Delete(path); } catch { /* do not disturb the report */ }
        Console.WriteLine();
        return failures == 0 ? 0 : 1;
    }

    private static ServerChunk BuildPatternChunk(ChunkDataPool pool)
    {
        var chunk = ServerChunk.CreateNew(pool);
        var data = chunk.Data;

        for (int y = 0; y < ChunkSize; y++)
        for (int z = 0; z < ChunkSize; z++)
        for (int x = 0; x < ChunkSize; x++)
        {
            data[Index(x, y, z)] = y switch
            {
                < 8 => 1,   // rock
                8 => 2,     // soil
                9 => 3,     // grass
                _ => 0      // air
            };
        }

        // Liquid in a corner — check that the liquid layer makes it through.
        for (int y = 0; y < 3; y++)
        for (int z = 0; z < 4; z++)
        for (int x = 0; x < 4; x++)
        {
            data.SetFluid(Index(x, y, z), 7);
        }

        return chunk;
    }

    private static int[] ExpectedPattern()
    {
        var expected = new int[BlocksPerChunk];
        for (int y = 0; y < ChunkSize; y++)
        for (int z = 0; z < ChunkSize; z++)
        for (int x = 0; x < ChunkSize; x++)
        {
            expected[Index(x, y, z)] = y switch { < 8 => 1, 8 => 2, 9 => 3, _ => 0 };
        }
        return expected;
    }

    private static int[] ReadBlocks(IWorldChunk chunk)
    {
        chunk.Unpack_ReadOnly();
        var data = chunk.Data;
        var result = new int[BlocksPerChunk];
        data.TakeBulkReadLock();
        try
        {
            for (int i = 0; i < result.Length; i++) result[i] = data.GetBlockId(i, BlockLayersAccess.SolidBlocks);
        }
        finally
        {
            data.ReleaseBulkReadLock();
        }
        return result;
    }

    private static int Compare(int[] expected, int[] actual)
    {
        int bad = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] == actual[i]) continue;
            if (bad < 3)
            {
                int x = i & 31, z = (i >> 5) & 31, y = (i >> 10) & 31;
                Console.WriteLine($"    index {i} (x={x},y={y},z={z}): expected {expected[i]}, got {actual[i]}");
            }
            bad++;
        }
        return bad;
    }

    /// <summary>internal ServerChunk.ToPacket(...) — the same code that builds packets on the server.</summary>
    private static Packet_ServerChunk BuildPacketViaReflection(ServerChunk chunk, int x, int y, int z)
    {
        var method = typeof(ServerChunk).GetMethod("ToPacket", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException("ServerChunk.ToPacket not found");
        return (Packet_ServerChunk)method.Invoke(chunk, [x, y, z, false]);
    }

    private static T GetField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)
            ?? instance.GetType().BaseType?.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return field == null ? default : (T)field.GetValue(instance);
    }

    private static bool SequenceEqual(byte[] a, byte[] b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    private static int Index(int x, int y, int z) => x | (z << 5) | (y << 10);

    private static int Len(byte[] b) => b?.Length ?? -1;

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
