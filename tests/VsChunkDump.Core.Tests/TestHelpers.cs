using VsChunkDump.Core;

namespace VsChunkDump.Core.Tests;

/// <summary>Temporary directory that is deleted after the test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string name)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"vschunkdump-test-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* do not let cleanup mask the test failure */ }
    }
}

/// <summary>Test data generators.</summary>
public static class TestData
{
    /// <summary>A chunk where the block is determined by a simple function of the coordinates.</summary>
    public static int[] PatternChunk(int baseX, int baseY, int baseZ, Func<int, int, int, int> f)
    {
        var blocks = new int[ChunkGeometry.BlocksPerChunk];
        for (int y = 0; y < ChunkGeometry.ChunkSize; y++)
        for (int z = 0; z < ChunkGeometry.ChunkSize; z++)
        for (int x = 0; x < ChunkGeometry.ChunkSize; x++)
        {
            blocks[ChunkGeometry.Index3d(x, y, z)] = f(baseX + x, baseY + y, baseZ + z);
        }
        return blocks;
    }

    /// <summary>Simple terrain: rock below the level, grass at the level, air above.</summary>
    public static ChunkRecord TerrainChunk(int cx, int cy, int cz, int groundY)
    {
        int baseY = cy * ChunkGeometry.ChunkSize;
        var blocks = new int[ChunkGeometry.BlocksPerChunk];
        for (int y = 0; y < ChunkGeometry.ChunkSize; y++)
        for (int z = 0; z < ChunkGeometry.ChunkSize; z++)
        for (int x = 0; x < ChunkGeometry.ChunkSize; x++)
        {
            int wy = baseY + y;
            int id = wy < groundY - 1 ? 1 /* rock */ : wy < groundY ? 2 /* soil */ : wy == groundY ? 3 /* grass */ : 0;
            blocks[ChunkGeometry.Index3d(x, y, z)] = id;
        }
        return new ChunkRecord { X = cx, Y = cy, Z = cz, Blocks = blocks };
    }

    public static DumpManifest Manifest(params string[] blockCodes) => new()
    {
        GameVersion = "1.22.7",
        ModVersion = "1.0.0",
        WorldName = "test-world",
        Seed = 42,
        BlockCodes = blockCodes.Length > 0 ? [.. blockCodes] : ["air", "game:rock-granite", "game:soil-medium-normal", "game:grass-medium-normal"]
    };
}
