namespace VsChunkDump.Core;

/// <summary>Render result: dimensions and RGBA pixels.</summary>
public readonly record struct RenderedImage(int Width, int Height, byte[] Pixels);

/// <summary>Top view: color of the top block + shading by height.</summary>
public static class TopDownRenderer
{
    public static RenderedImage Render(SurfaceModel model, int pixelsPerBlock = 2, bool shadeByHeight = true,
        bool chunkGrid = false, BlockTable? table = null)
    {
        table ??= BlockTable.Empty;
        int s = Math.Max(1, pixelsPerBlock);
        int w = model.Width * s;
        int h = model.Depth * s;
        var canvas = new Canvas(w, h, (16, 16, 20, 255));

        var (minY, maxY) = model.HeightRange();
        float span = Math.Max(1, maxY - minY);

        for (int z = 0; z < model.Depth; z++)
        {
            for (int x = 0; x < model.Width; x++)
            {
                int i = z * model.Width + x;
                int id = model.TopBlockId[i];
                if (id < 0) continue;

                var color = BlockColorizer.ColorOf(id, table);
                if (color.A == 0) continue;
                if (shadeByHeight)
                {
                    float t = (model.TopY[i] - minY) / span;
                    color = BlockColorizer.Shade(color, 0.62f + 0.38f * t);
                }
                canvas.FillRect(x * s, z * s, s, s, color);
            }
        }

        if (chunkGrid)
        {
            var line = ((byte)255, (byte)255, (byte)255, (byte)70);
            for (int wx = model.Bounds.MinX; wx <= model.Bounds.MaxX + 1; wx += ChunkGeometry.ChunkSize)
            {
                int px = (wx - model.Bounds.MinX) * s;
                for (int y = 0; y < h; y++) canvas.Blend(px, y, line);
            }
            for (int wz = model.Bounds.MinZ; wz <= model.Bounds.MaxZ + 1; wz += ChunkGeometry.ChunkSize)
            {
                int py = (wz - model.Bounds.MinZ) * s;
                for (int x = 0; x < w; x++) canvas.Blend(x, py, line);
            }
        }

        return new RenderedImage(w, h, canvas.Pixels);
    }
}

/// <summary>Horizontal slice at a given height — handy for inspecting building layouts.</summary>
public static class SliceRenderer
{
    public static RenderedImage Render(DumpSet set, int dim, int y, BlockBounds bounds, BlockTable table,
        int pixelsPerBlock = 2)
    {
        int s = Math.Max(1, pixelsPerBlock);
        int w = bounds.Width * s;
        int h = bounds.Depth * s;
        var canvas = new Canvas(w, h, (16, 16, 20, 255));

        int chunkY = ChunkGeometry.FloorDiv(y, ChunkGeometry.ChunkSize);
        int localY = y - chunkY * ChunkGeometry.ChunkSize;

        foreach (var chunk in set.ReadChunks(dim))
        {
            if (chunk.Y != chunkY) continue;
            int baseX = chunk.X * ChunkGeometry.ChunkSize;
            int baseZ = chunk.Z * ChunkGeometry.ChunkSize;

            for (int lz = 0; lz < ChunkGeometry.ChunkSize; lz++)
            {
                int wz = baseZ + lz;
                if (wz < bounds.MinZ || wz > bounds.MaxZ) continue;
                for (int lx = 0; lx < ChunkGeometry.ChunkSize; lx++)
                {
                    int wx = baseX + lx;
                    if (wx < bounds.MinX || wx > bounds.MaxX) continue;

                    int index = ChunkGeometry.Index3d(lx, localY, lz);
                    int id = chunk.Blocks[index];
                    if (id == 0 && chunk.Fluids != null) id = chunk.Fluids[index];
                    if (id == 0) continue;

                    var color = BlockColorizer.ColorOf(id, table);
                    if (color.A == 0) continue;
                    canvas.FillRect((wx - bounds.MinX) * s, (wz - bounds.MinZ) * s, s, s, color);
                }
            }
        }

        return new RenderedImage(w, h, canvas.Pixels);
    }
}

/// <summary>
/// Isometric (2:1) surface view. A column is drawn as a diamond on top plus
/// two side faces down to the height of the lowest front neighbor.
///
/// Limitation: the view is built from the heightmap, so overhangs (arches, roofs
/// over passages) are not visible — only the top of each column is drawn.
/// </summary>
public static class IsometricRenderer
{
    public static RenderedImage Render(SurfaceModel model, BlockTable table,
        int tileWidth = 6, int yScale = 3, bool shadeByHeight = true)
    {
        int tw = Math.Max(2, tileWidth);
        if (tw % 2 != 0) tw++;
        int th = Math.Max(1, tw / 2);
        int W = model.Width, D = model.Depth;

        int maxS = (W - 1) + (D - 1);
        int width = maxS * (tw / 2) + tw + 2;

        var (minY, maxY) = model.HeightRange();
        int ySpan = Math.Max(0, maxY - minY);
        int height = maxS * (th / 2) + ySpan * yScale + th + 2;
        if (height <= 0 || width <= 0) throw new InvalidOperationException("Empty area for isometric view");

        int ox = (D - 1) * (tw / 2) + tw / 2;
        int oy = ySpan * yScale + th / 2 + 1;

        var canvas = new Canvas(width, height, (18, 20, 26, 255));
        float span = Math.Max(1, ySpan);

        int TopYOf(int rx, int rz)
        {
            if ((uint)rx >= (uint)W || (uint)rz >= (uint)D) return int.MinValue;
            int i = rz * W + rx;
            return model.TopBlockId[i] < 0 ? int.MinValue : model.TopY[i];
        }

        // Painter's order: from far columns to near ones (by the sum rx+rz).
        for (int s = 0; s <= maxS; s++)
        {
            int rxStart = Math.Max(0, s - (D - 1));
            int rxEnd = Math.Min(W - 1, s);
            for (int rx = rxStart; rx <= rxEnd; rx++)
            {
                int rz = s - rx;
                int i = rz * W + rx;
                int id = model.TopBlockId[i];
                if (id < 0) continue;

                var baseColor = BlockColorizer.ColorOf(id, table);
                if (baseColor.A == 0) continue;

                int topY = model.TopY[i];
                int sx = (rx - rz) * (tw / 2) + ox;
                int sy = (rx + rz) * (th / 2) - (topY - minY) * yScale + oy;

                int n1 = TopYOf(rx + 1, rz);
                int n2 = TopYOf(rx, rz + 1);
                int baseY = topY;
                if (n1 != int.MinValue) baseY = Math.Min(baseY, n1);
                if (n2 != int.MinValue) baseY = Math.Min(baseY, n2);
                int drop = Math.Max(0, topY - baseY);
                int dropPx = drop * yScale;

                float lightness = shadeByHeight ? 0.72f + 0.28f * ((topY - minY) / span) : 1f;

                // Side faces (only if the column is taller than the front neighbors).
                if (dropPx > 0)
                {
                    var rightFace = BlockColorizer.Shade(baseColor, lightness * 0.68f);
                    canvas.FillQuad(
                        (sx, sy),
                        (sx + tw / 2f, sy + th / 2f),
                        (sx + tw / 2f, sy + th / 2f + dropPx),
                        (sx, sy + dropPx), rightFace);

                    var leftFace = BlockColorizer.Shade(baseColor, lightness * 0.52f);
                    canvas.FillQuad(
                        (sx - tw / 2f, sy + th / 2f),
                        (sx, sy),
                        (sx, sy + dropPx),
                        (sx - tw / 2f, sy + th / 2f + dropPx), leftFace);
                }

                canvas.FillDiamond(sx, sy, tw / 2, th / 2, BlockColorizer.Shade(baseColor, lightness * 1.05f));
            }
        }

        return new RenderedImage(width, height, canvas.Pixels);
    }
}
