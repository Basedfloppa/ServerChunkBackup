namespace VsChunkDump.Core;

/// <summary>
/// One saved chunk: a 32x32x32 block grid plus (optionally) the liquids layer.
/// </summary>
public sealed class ChunkRecord
{
    /// <summary>Dimension (0 — the regular world).</summary>
    public int Dim;

    /// <summary>Chunk coordinates (in chunks, not in blocks).</summary>
    public int X, Y, Z;

    /// <summary>Block ids of the solid layer, length <see cref="ChunkGeometry.BlocksPerChunk"/>.</summary>
    public int[] Blocks = [];

    /// <summary>Block ids of the liquids layer (may be null).</summary>
    public int[]? Fluids;

    /// <summary>Chunk uniqueness key.</summary>
    public long Key => PackKey(Dim, X, Y, Z);

    public static long PackKey(int dim, int x, int y, int z)
        => ((long)(uint)dim << 48) | ((long)(uint)(x & 0xFFFF) << 32)
         | ((long)(uint)(y & 0xFFFF) << 16) | (uint)(z & 0xFFFF);
}
