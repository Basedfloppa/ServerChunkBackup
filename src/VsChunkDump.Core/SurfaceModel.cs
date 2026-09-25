namespace VsChunkDump.Core;

/// <summary>Rectangular area in blocks.</summary>
public readonly record struct BlockBounds(int MinX, int MinZ, int MaxX, int MaxZ)
{
    public int Width => MaxX - MinX + 1;
    public int Depth => MaxZ - MinZ + 1;

    public bool Contains(int x, int z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
}

/// <summary>
/// World 'surface': for each column (x,z), the topmost non-empty block.
/// Built in a single streaming pass over the dump, so it needs
/// O(area) memory, not O(volume).
/// </summary>
public sealed class SurfaceModel
{
    public int Dim;
    public int MapSizeY = ChunkGeometry.DefaultMapSizeY;

    public BlockBounds Bounds;
    public int Width => Bounds.Width;
    public int Depth => Bounds.Depth;

    /// <summary>Top block id; -1 means there is no data for the column.</summary>
    public int[] TopBlockId = [];

    /// <summary>World Y of the top block.</summary>
    public int[] TopY = [];

    /// <summary>How many blocks of each type occurred across all layers.</summary>
    public readonly Dictionary<int, long> Histogram = [];

    public long ChunksRead;
    public long TotalBlocks;

    public int Index(int x, int z) => (z - Bounds.MinZ) * Width + (x - Bounds.MinX);

    /// <summary>
    /// Build the model. If <paramref name="crop"/> is not set, the area is taken
    /// from the actual chunk bounds in the dump.
    /// </summary>
    public static SurfaceModel Build(DumpSet set, int dim, BlockBounds? crop = null, bool includeFluids = true)
    {
        BlockBounds bounds;
        if (crop is { } c)
        {
            bounds = c;
        }
        else
        {
            int minCx = int.MaxValue, maxCx = int.MinValue, minCz = int.MaxValue, maxCz = int.MinValue;
            bool any = false;
            foreach (var (x, _, z) in set.ChunkCoords(dim))
            {
                any = true;
                minCx = Math.Min(minCx, x);
                maxCx = Math.Max(maxCx, x);
                minCz = Math.Min(minCz, z);
                maxCz = Math.Max(maxCz, z);
            }
            if (!any)
                throw new InvalidOperationException($"The dump has no chunks for dimension {dim}");

            bounds = new BlockBounds(
                minCx * ChunkGeometry.ChunkSize,
                minCz * ChunkGeometry.ChunkSize,
                maxCx * ChunkGeometry.ChunkSize + ChunkGeometry.ChunkSize - 1,
                maxCz * ChunkGeometry.ChunkSize + ChunkGeometry.ChunkSize - 1);
        }

        var model = new SurfaceModel
        {
            Dim = dim,
            Bounds = bounds,
            MapSizeY = set.Manifest.MapSizeY,
            TopBlockId = new int[bounds.Width * bounds.Depth],
            TopY = new int[bounds.Width * bounds.Depth]
        };
        Array.Fill(model.TopBlockId, -1);

        foreach (var chunk in set.ReadChunks(dim))
        {
            model.ChunksRead++;
            int baseX = chunk.X * ChunkGeometry.ChunkSize;
            int baseY = chunk.Y * ChunkGeometry.ChunkSize;
            int baseZ = chunk.Z * ChunkGeometry.ChunkSize;

            for (int lz = 0; lz < ChunkGeometry.ChunkSize; lz++)
            {
                int wz = baseZ + lz;
                if (wz < bounds.MinZ || wz > bounds.MaxZ) continue;
                int zRow = (wz - bounds.MinZ) * model.Width;

                for (int lx = 0; lx < ChunkGeometry.ChunkSize; lx++)
                {
                    int wx = baseX + lx;
                    if (wx < bounds.MinX || wx > bounds.MaxX) continue;
                    int col = zRow + (wx - bounds.MinX);

                    for (int ly = 0; ly < ChunkGeometry.ChunkSize; ly++)
                    {
                        int index = ChunkGeometry.Index3d(lx, ly, lz);
                        int solid = chunk.Blocks[index];
                        int id = solid != 0 ? solid : (includeFluids && chunk.Fluids != null ? chunk.Fluids[index] : 0);
                        if (id == 0) continue;

                        model.TotalBlocks++;
                        model.Histogram[id] = model.Histogram.GetValueOrDefault(id) + 1;

                        int wy = baseY + ly;
                        if (wy > model.TopY[col])
                        {
                            model.TopY[col] = wy;
                            model.TopBlockId[col] = id;
                        }
                    }
                }
            }
        }

        return model;
    }

    public (int MinY, int MaxY) HeightRange()
    {
        int min = int.MaxValue, max = int.MinValue;
        for (int i = 0; i < TopBlockId.Length; i++)
        {
            if (TopBlockId[i] < 0) continue;
            min = Math.Min(min, TopY[i]);
            max = Math.Max(max, TopY[i]);
        }
        if (min == int.MaxValue) return (0, 0);
        return (min, max);
    }
}
