namespace VsChunkDump.Core;

/// <summary>
/// Vintage Story chunk geometry.
/// Values verified against the game sources:
///   GlobalConstants.ChunkSize = 32
///   ChunkData.Length = 32768            (32*32*32)
///   MagicNum.ChunkRegionSizeInChunks = 16
///   ServerConfig.MapSizeY = 256  =>  256/32 = 8 sections vertically
/// </summary>
public static class ChunkGeometry
{
    /// <summary>Chunk side length in blocks (GlobalConstants.ChunkSize).</summary>
    public const int ChunkSize = 32;

    /// <summary>Blocks in one chunk (32^3).</summary>
    public const int BlocksPerChunk = ChunkSize * ChunkSize * ChunkSize; // 32768

    /// <summary>How many chunks along X/Z fit in one region (MagicNum.ChunkRegionSizeInChunks).</summary>
    public const int RegionSizeInChunks = 16;

    /// <summary>Default world height (ServerConfig.MapSizeY).</summary>
    public const int DefaultMapSizeY = 256;

    /// <summary>Vertical sections per column by default (MapSizeY / ChunkSize).</summary>
    public const int DefaultSectionCountY = DefaultMapSizeY / ChunkSize; // 8

    /// <summary>
    /// Block index inside a chunk. Formula from the <c>IWorldChunk.Data</c> docs:
    /// <c>(y * chunksize + z) * chunksize + x</c>, all coordinates local 0..31.
    /// </summary>
    public static int Index3d(int x, int y, int z) => (y * ChunkSize + z) * ChunkSize + x;

    /// <summary>Recover local coordinates from a block index.</summary>
    public static (int X, int Y, int Z) UnIndex3d(int index3d)
    {
        int x = index3d % ChunkSize;
        int rest = index3d / ChunkSize;
        int z = rest % ChunkSize;
        int y = rest / ChunkSize;
        return (x, y, z);
    }

    /// <summary>Region number for a chunk coordinate (may be negative).</summary>
    public static int RegionOf(int chunkCoord) => FloorDiv(chunkCoord, RegionSizeInChunks);

    /// <summary>Floor division (correct for negative coordinates).</summary>
    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -(((-a) + b - 1) / b);

    /// <summary>Floor remainder, always 0..b-1.</summary>
    public static int FloorMod(int a, int b)
    {
        int r = a % b;
        return r < 0 ? r + b : r;
    }

    /// <summary>Unpack a flat chunk index (long) into coordinates — as in <c>IWorldAccessor.LoadedChunkIndices</c>.</summary>
    public static (int X, int Y, int Z) UnpackChunkIndex(long index3d, int chunkMapSizeX, int chunkMapSizeZ)
    {
        int x = (int)(index3d % chunkMapSizeX);
        int y = (int)(index3d / ((long)chunkMapSizeX * chunkMapSizeZ));
        int z = (int)((index3d / chunkMapSizeX) % chunkMapSizeZ);
        return (x, y, z);
    }
}
