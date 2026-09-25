using System.IO.Compression;
using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

public class PngEncoderTests
{
    [Fact]
    public void Encode_HasValidSignatureAndHeader()
    {
        var rgba = new byte[4 * 4 * 4];
        var png = PngEncoder.EncodeRgba(4, 4, rgba);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        var chunks = ParseChunks(png);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type).ToArray());

        var ihdr = chunks[0].Data;
        Assert.Equal(4, ReadInt32BE(ihdr, 0));
        Assert.Equal(4, ReadInt32BE(ihdr, 4));
        Assert.Equal(8, ihdr[8]);   // bit depth
        Assert.Equal(6, ihdr[9]);   // RGBA
        Assert.Equal(0, ihdr[12]);  // no interlacing
    }

    [Fact]
    public void Encode_PixelDataSurvivesRoundTrip()
    {
        const int w = 3, h = 2;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 7 % 256);

        var png = PngEncoder.EncodeRgba(w, h, rgba);
        var chunks = ParseChunks(png);
        var idat = chunks.Single(c => c.Type == "IDAT").Data;

        using var ms = new MemoryStream(idat);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var scanlines = raw.ToArray();

        Assert.Equal(h * (1 + w * 4), scanlines.Length);
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(0, scanlines[y * (1 + w * 4)]); // filter None
            for (int i = 0; i < w * 4; i++)
            {
                Assert.Equal(rgba[y * w * 4 + i], scanlines[y * (1 + w * 4) + 1 + i]);
            }
        }
    }

    [Fact]
    public void Encode_RejectsWrongBufferSize()
        => Assert.Throws<ArgumentException>(() => PngEncoder.EncodeRgba(2, 2, new byte[5]));

    [Fact]
    public void EveryChunkCrcIsValid()
    {
        var png = PngEncoder.EncodeRgba(8, 8, new byte[8 * 8 * 4]);
        foreach (var (type, data, offset) in ParseChunksWithOffsets(png))
        {
            uint stored = ReadUInt32BE(png, offset + 8 + data.Length);
            uint computed = Crc32.Compute(png.AsSpan(offset + 4, 4 + data.Length));
            Assert.Equal(computed, stored);
        }
    }

    internal static List<(string Type, byte[] Data, int Offset)> ParseChunksWithOffsets(byte[] png)
    {
        var list = new List<(string, byte[], int)>();
        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int len = ReadInt32BE(png, pos);
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len).ToArray();
            list.Add((type, data, pos));
            pos += 12 + len;
        }
        return list;
    }

    internal static List<(string Type, byte[] Data)> ParseChunks(byte[] png)
        => ParseChunksWithOffsets(png).Select(c => (c.Type, c.Data)).ToList();

    internal static int ReadInt32BE(byte[] b, int o)
        => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    internal static uint ReadUInt32BE(byte[] b, int o)
        => (uint)ReadInt32BE(b, o);
}

public class BlockColorizerTests
{
    [Fact]
    public void Air_IsFullyTransparent()
        => Assert.Equal(0, BlockColorizer.ColorOf("game:air").A);

    [Theory]
    [InlineData("game:water-still-7", true)]
    [InlineData("game:soil-medium-normal", false)]
    [InlineData("game:rock-granite", false)]
    [InlineData("game:leaves-oak-grown", false)]
    public void Water_IsBlueDominant_OthersAreNot(string code, bool expectBlueDominant)
    {
        var c = BlockColorizer.ColorOf(code);
        bool blueDominant = c.B > c.R;
        Assert.Equal(expectBlueDominant, blueDominant);
    }

    [Fact]
    public void UnknownCodes_AreStableAndOpaque()
    {
        var a = BlockColorizer.ColorOf("somemod:totally-unknown-thing");
        var b = BlockColorizer.ColorOf("somemod:totally-unknown-thing");
        Assert.Equal(a, b);
        Assert.Equal(255, a.A);
    }

    [Fact]
    public void DifferentUnknownCodes_DifferVisually()
    {
        var a = BlockColorizer.ColorOf("moda:block-one");
        var b = BlockColorizer.ColorOf("moda:block-two");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void WoodSpecies_AreTintedDifferently()
    {
        var birch = BlockColorizer.ColorOf("game:log-birch-grown");
        var ebony = BlockColorizer.ColorOf("game:log-ebony-grown");
        Assert.True(birch.R > ebony.R, "birch should be lighter than ebony wood");
    }

    [Fact]
    public void Shade_ScalesChannelsAndKeepsAlpha()
    {
        var c = BlockColorizer.Shade((200, 100, 50, 128), 0.5f);
        Assert.Equal((100, 50, 25, 128), c);
    }

    [Fact]
    public void Shade_ClampsToByteRange()
    {
        var c = BlockColorizer.Shade((200, 200, 200, 255), 10f);
        Assert.Equal(255, c.R);
    }
}

public class RendererTests
{
    private const int BackgroundRgb = (16 << 16) | (16 << 8) | 20;

    /// <summary>Build a dump of <paramref name="columns"/>^2 columns with height <paramref name="groundAt"/>.</summary>
    private static DumpSet BuildTerrainDump(TempDir tmp, Func<int, int, int> groundAt, int columns = 2)
    {
        using var writer = new DumpWriter(tmp.Path, TestData.Manifest());
        for (int cy = 0; cy < 4; cy++)
        for (int cz = 0; cz < columns; cz++)
        for (int cx = 0; cx < columns; cx++)
        {
            writer.TryWriteChunk(TestData.TerrainChunk(cx, cy, cz, groundAt(cx, cz)));
        }
        writer.Flush();
        return DumpSet.Open(tmp.Path);
    }

    private static DumpSet BuildFlatTerrainDump(TempDir tmp, int groundY = 70, int columns = 2)
        => BuildTerrainDump(tmp, (_, _) => groundY, columns);

    [Fact]
    public void TopDown_ProducesExpectedSizeAndColours()
    {
        using var tmp = new TempDir("topdown");
        // Uneven terrain so that height shading is noticeable.
        var set = BuildTerrainDump(tmp, (cx, cz) => 70 + cx * 2 + cz * 3, columns: 2);
        var model = SurfaceModel.Build(set, 0);

        var img = TopDownRenderer.Render(model, pixelsPerBlock: 2, shadeByHeight: true, chunkGrid: false, table: set.Blocks);

        Assert.Equal(64 * 2, img.Width);
        Assert.Equal(64 * 2, img.Height);
        Assert.Equal(img.Width * img.Height * 4, img.Pixels.Length);

        // The top block is grass, so the color should be 'green' (G > R and G > B).
        int o = ((20 * 2) * img.Width + 20 * 2) * 4;
        Assert.True(img.Pixels[o + 1] > img.Pixels[o], "the G channel should dominate R on grass");
        Assert.True(img.Pixels[o + 1] > img.Pixels[o + 2], "the G channel should dominate B on grass");

        Assert.True(DistinctColors(img) > 1, "different terrain heights should give different shades");
    }

    [Fact]
    public void TopDown_FlatTerrain_HasSingleShade()
    {
        using var tmp = new TempDir("topdownflat");
        var set = BuildFlatTerrainDump(tmp);
        var model = SurfaceModel.Build(set, 0);

        var img = TopDownRenderer.Render(model, 2, true, false, set.Blocks);
        Assert.Equal(1, DistinctColors(img));
    }

    [Fact]
    public void TopDown_GridAddsVisibleLines()
    {
        using var tmp = new TempDir("grid");
        var set = BuildFlatTerrainDump(tmp);
        var model = SurfaceModel.Build(set, 0);

        var plain = TopDownRenderer.Render(model, 1, true, false, set.Blocks);
        var grid = TopDownRenderer.Render(model, 1, true, true, set.Blocks);

        Assert.NotEqual(plain.Pixels, grid.Pixels);
    }

    [Fact]
    public void Iso_RendersNonEmptyImage()
    {
        using var tmp = new TempDir("iso");
        // Stepped terrain => side faces with different shading should appear.
        var set = BuildTerrainDump(tmp, (cx, cz) => 70 + cx * 3 + cz * 2, columns: 2);
        var model = SurfaceModel.Build(set, 0);

        var img = IsometricRenderer.Render(model, set.Blocks, tileWidth: 8, yScale: 3);

        Assert.True(img.Width > 0 && img.Height > 0);
        Assert.True(DistinctColors(img) > 3, "the isometric view should contain several shades (top + side faces)");
    }

    [Fact]
    public void Iso_FlatTerrainHasNoSideFaces()
    {
        using var tmp = new TempDir("isoflat");
        var set = BuildFlatTerrainDump(tmp);
        var model = SurfaceModel.Build(set, 0);

        // Flat terrain: the height is the same => there should be no side faces,
        // so no extra (dark side) shades will appear.
        var img = IsometricRenderer.Render(model, set.Blocks, tileWidth: 8, yScale: 3, shadeByHeight: false);
        var colors = DistinctColors(img);
        Assert.Equal(2, colors); // background + grass
    }

    [Fact]
    public void Slice_AboveTerrain_IsBackgroundOnly()
    {
        using var tmp = new TempDir("slice");
        var set = BuildFlatTerrainDump(tmp, groundY: 70);
        var bounds = new BlockBounds(0, 0, 63, 63);

        var empty = SliceRenderer.Render(set, 0, y: 200, bounds, set.Blocks, 1);
        Assert.Equal([BackgroundRgb], DistinctColorSet(empty).ToArray());

        // At wy=69 there is soil (id 2) — the whole slice should be filled with it.
        var solid = SliceRenderer.Render(set, 0, y: 69, bounds, set.Blocks, 1);
        Assert.DoesNotContain(BackgroundRgb, DistinctColorSet(solid));

        // At wy=70 there is grass; the color should differ from soil.
        var grass = SliceRenderer.Render(set, 0, y: 70, bounds, set.Blocks, 1);
        Assert.NotEqual(DistinctColorSet(solid), DistinctColorSet(grass));
    }

    private static int DistinctColors(RenderedImage img) => DistinctColorSet(img).Count;

    private static HashSet<int> DistinctColorSet(RenderedImage img)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < img.Pixels.Length; i += 4)
        {
            set.Add((img.Pixels[i] << 16) | (img.Pixels[i + 1] << 8) | img.Pixels[i + 2]);
        }
        return set;
    }
}

/// <summary>End-to-end run: a synthetic dump on disk -&gt; all three renders -&gt; valid PNGs.</summary>
public class EndToEndTests
{
    [Fact]
    public void FullPipeline_ProducesValidPngFiles()
    {
        using var tmp = new TempDir("e2e");
        string dumpDir = Path.Combine(tmp.Path, "dump");
        string outDir = Path.Combine(tmp.Path, "out");

        // A small 'world' of 2x2 chunks with uneven terrain and a little stone house.
        var manifest = TestData.Manifest();
        using (var writer = new DumpWriter(dumpDir, manifest))
        {
            for (int cy = 0; cy < 3; cy++)
            for (int cz = 0; cz < 2; cz++)
            for (int cx = 0; cx < 2; cx++)
            {
                int section = cy;
                int ground = 70 + (cx == 1 && cz == 1 ? 3 : 0);
                var chunk = TestData.TerrainChunk(cx, section, cz, ground);

                // The 'house': on the top terrain layer we place brick along an 8x8 perimeter.
                if (section == 2)
                {
                    int baseY = 2 * ChunkGeometry.ChunkSize;
                    for (int y = 0; y < ChunkGeometry.ChunkSize; y++)
                    for (int z = 0; z < ChunkGeometry.ChunkSize; z++)
                    for (int x = 0; x < ChunkGeometry.ChunkSize; x++)
                    {
                        int wy = baseY + y;
                        bool wall = wy > ground && wy <= ground + 4
                                    && x is >= 4 and <= 11 && z is >= 4 and <= 11
                                    && (x == 4 || x == 11 || z == 4 || z == 11);
                        if (wall)
                        {
                            chunk.Blocks[ChunkGeometry.Index3d(x, y, z)] = 5; // brick
                        }
                    }
                }
                writer.TryWriteChunk(chunk);
            }
            writer.Flush();
        }

        var set = DumpSet.Open(dumpDir);
        var model = SurfaceModel.Build(set, 0);
        var bounds = model.Bounds;

        var top = TopDownRenderer.Render(model, 2, true, true, set.Blocks);
        var iso = IsometricRenderer.Render(model, set.Blocks, 6, 3);
        var slice = SliceRenderer.Render(set, 0, 72, bounds, set.Blocks, 2);

        string topPath = Path.Combine(outDir, "top.png");
        string isoPath = Path.Combine(outDir, "iso.png");
        string slicePath = Path.Combine(outDir, "slice.png");
        PngEncoder.WriteRgba(topPath, top.Width, top.Height, top.Pixels);
        PngEncoder.WriteRgba(isoPath, iso.Width, iso.Height, iso.Pixels);
        PngEncoder.WriteRgba(slicePath, slice.Width, slice.Height, slice.Pixels);

        foreach (string p in new[] { topPath, isoPath, slicePath })
        {
            Assert.True(File.Exists(p), $"not created: {p}");
            var bytes = File.ReadAllBytes(p);
            Assert.True(bytes.Length > 100, $"{p} is suspiciously small");
            var chunks = PngEncoderTests.ParseChunks(bytes);
            Assert.Equal("IHDR", chunks[0].Type);
            Assert.Equal("IEND", chunks[^1].Type);
        }

        // The histogram should contain the brick we placed (id 5).
        Assert.True(model.Histogram.ContainsKey(5), "the brick from the 'house' should make it into the dump");
    }
}
