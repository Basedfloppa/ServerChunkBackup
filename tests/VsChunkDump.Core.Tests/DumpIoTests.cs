using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

public class DumpIoTests
{
    [Fact]
    public void WriteThenRead_RoundTripsChunks()
    {
        using var tmp = new TempDir("io");
        var manifest = TestData.Manifest();
        var expected = new List<ChunkRecord>();

        using (var writer = new DumpWriter(tmp.Path, manifest))
        {
            // Three sections of one column + one chunk in a neighboring region (cx = 16).
            foreach (var (cx, cy, cz) in new[] { (0, 0, 0), (0, 1, 0), (0, 2, 0), (16, 0, 3), (-1, 0, -1) })
            {
                var chunk = TestData.TerrainChunk(cx, cy, cz, groundY: 70);
                expected.Add(chunk);
                Assert.True(writer.TryWriteChunk(chunk));
            }
            writer.Flush();
        }

        var set = DumpSet.Open(tmp.Path);
        var read = set.ReadChunks().ToList();

        Assert.Equal(expected.Count, read.Count);
        foreach (var want in expected)
        {
            var got = read.Single(c => c.X == want.X && c.Y == want.Y && c.Z == want.Z);
            Assert.Equal(want.Blocks, got.Blocks);
        }
    }

    [Fact]
    public void WritingSameChunkTwice_IsIgnored()
    {
        using var tmp = new TempDir("dedupe");
        using var writer = new DumpWriter(tmp.Path, TestData.Manifest());

        var chunk = TestData.TerrainChunk(2, 0, 2, 64);
        Assert.True(writer.TryWriteChunk(chunk));
        Assert.False(writer.TryWriteChunk(chunk));
        Assert.True(writer.HasChunk(0, 2, 0, 2));
        Assert.Equal(1, writer.UniqueChunks);
        writer.Flush();

        var set = DumpSet.Open(tmp.Path);
        Assert.Single(set.ReadChunks());
    }

    [Fact]
    public void ReopeningDump_ResumesWithoutDuplicateWrites()
    {
        using var tmp = new TempDir("resume");
        var chunk = TestData.TerrainChunk(1, 0, 1, 70);

        using (var w1 = new DumpWriter(tmp.Path, TestData.Manifest()))
        {
            Assert.True(w1.TryWriteChunk(chunk));
            w1.Flush();
        }

        // A new 'session' — the mod was restarted, the dump is the same.
        using (var w2 = new DumpWriter(tmp.Path, TestData.Manifest()))
        {
            Assert.True(w2.HasChunk(0, 1, 0, 1));
            Assert.False(w2.TryWriteChunk(chunk));
            Assert.True(w2.TryWriteChunk(TestData.TerrainChunk(1, 1, 1, 70)));
            w2.Flush();
        }

        var set = DumpSet.Open(tmp.Path);
        Assert.Equal(2, set.ReadChunks().Count());
    }

    [Fact]
    public void TryGetChunk_FindsChunkAndReportsMissing()
    {
        using var tmp = new TempDir("get");
        using (var writer = new DumpWriter(tmp.Path, TestData.Manifest()))
        {
            writer.TryWriteChunk(TestData.TerrainChunk(0, 0, 0, 70));
            writer.Flush();
        }

        var set = DumpSet.Open(tmp.Path);
        Assert.True(set.TryGetChunk(0, 0, 0, 0, out var chunk));
        Assert.Equal(0, chunk.Y);
        Assert.False(set.TryGetChunk(0, 5, 0, 5, out _));
        // The region exists, but it does not contain the requested section.
        Assert.False(set.TryGetChunk(0, 0, 3, 0, out _));
    }

    [Fact]
    public void CorruptedPayload_IsDetectedByCrc()
    {
        using var tmp = new TempDir("corrupt");
        using (var writer = new DumpWriter(tmp.Path, TestData.Manifest()))
        {
            writer.TryWriteChunk(TestData.TerrainChunk(0, 0, 0, 70));
            writer.Flush();
        }

        string file = DumpFormat.RegionFilePath(tmp.Path, 0, 0, 0);
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 0xFF; // corrupt the last payload byte
        File.WriteAllBytes(file, bytes);

        var set = DumpSet.Open(tmp.Path);
        Assert.Throws<InvalidDataException>(() => set.ReadChunks().ToList());
    }

    [Fact]
    public void CorruptedHeader_FailsFast()
    {
        using var tmp = new TempDir("badheader");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "dim0"));
        string file = DumpFormat.RegionFilePath(tmp.Path, 0, 0, 0);
        File.WriteAllBytes(file, new byte[64]); // no "VSRG" magic

        TestData.Manifest().Save(tmp.Path);
        var set = DumpSet.Open(tmp.Path);
        Assert.Throws<InvalidDataException>(() => set.ReadChunks().ToList());
    }

    [Fact]
    public void ChunkCoords_DoesNotDecodePayloads()
    {
        using var tmp = new TempDir("coords");
        using (var writer = new DumpWriter(tmp.Path, TestData.Manifest()))
        {
            writer.TryWriteChunk(TestData.TerrainChunk(3, 0, 4, 70));
            writer.TryWriteChunk(TestData.TerrainChunk(3, 1, 4, 70));
            writer.Flush();
        }

        // Corrupt only the tail of the file — that is the payload of the last record.
        // The record headers stay intact, so coordinate enumeration
        // (which does not read the payload) must keep working.
        string file = DumpFormat.RegionFilePath(tmp.Path, 0, 0, 0);
        var bytes = File.ReadAllBytes(file);
        for (int i = bytes.Length - 8; i < bytes.Length; i++) bytes[i] = 0xAB;
        File.WriteAllBytes(file, bytes);

        var set = DumpSet.Open(tmp.Path);
        var coords = set.ChunkCoords(0).ToList();
        Assert.Equal(2, coords.Count);
        Assert.Contains((3, 0, 4), coords);
        Assert.Contains((3, 1, 4), coords);
    }

    [Fact]
    public void Manifest_SavesAndLoadsAllFields()
    {
        using var tmp = new TempDir("manifest");
        var m = TestData.Manifest();
        m.ServerAddress = "play.example.com:42420";
        m.SavegameIdentifier = "abc-123";
        m.SectionCountY = 8;
        m.Save(tmp.Path);

        var loaded = DumpManifest.Load(tmp.Path);
        Assert.Equal(DumpFormat.FormatName, loaded.Format);
        Assert.Equal(DumpFormat.FormatVersion, loaded.FormatVersion);
        Assert.Equal("play.example.com:42420", loaded.ServerAddress);
        Assert.Equal("abc-123", loaded.SavegameIdentifier);
        Assert.Equal(42, loaded.Seed);
        Assert.Equal(4, loaded.BlockCodes.Count);
    }

    [Fact]
    public void Manifest_MissingFile_Throws()
    {
        using var tmp = new TempDir("nomanifest");
        Assert.Throws<FileNotFoundException>(() => DumpManifest.Load(tmp.Path));
    }
}

public class SurfaceModelTests
{
    [Fact]
    public void Build_FindsTopBlockAndHeight()
    {
        using var tmp = new TempDir("surface");
        using (var writer = new DumpWriter(tmp.Path, TestData.Manifest()))
        {
            // groundY = 70 => the top block (grass, id 3) is at height 70.
            writer.TryWriteChunk(TestData.TerrainChunk(0, 0, 0, 70));
            writer.TryWriteChunk(TestData.TerrainChunk(0, 1, 0, 70));
            writer.TryWriteChunk(TestData.TerrainChunk(0, 2, 0, 70));
            writer.Flush();
        }

        var set = DumpSet.Open(tmp.Path);
        var model = SurfaceModel.Build(set, 0);

        Assert.Equal(32, model.Width);
        Assert.Equal(32, model.Depth);
        Assert.Equal(3, model.ChunksRead);

        var (minY, maxY) = model.HeightRange();
        Assert.Equal(70, minY);
        Assert.Equal(70, maxY);

        // Column (5,7): grass on top (id 3).
        Assert.Equal(3, model.TopBlockId[model.Index(5, 7)]);
        Assert.Equal(70, model.TopY[model.Index(5, 7)]);

        // id 1 (rock) fills every layer below 69: 32 layers each in sections 0 and 1 plus 5 layers
        // in section 2 => 32768 + 32768 + 5*1024 = 70656.
        // id 2 (soil) — exactly one layer at wy=69, id 3 (grass) — exactly one layer at wy=70.
        Assert.Equal(70656, model.Histogram[1]);
        Assert.Equal(1024, model.Histogram[2]);
        Assert.Equal(1024, model.Histogram[3]);
        Assert.Equal(70656 + 1024 + 1024, model.TotalBlocks);
    }

    [Fact]
    public void Build_HonoursCrop()
    {
        using var tmp = new TempDir("crop");
        using (var writer = new DumpWriter(tmp.Path, TestData.Manifest()))
        {
            for (int cy = 0; cy < 3; cy++)
            {
                writer.TryWriteChunk(TestData.TerrainChunk(0, cy, 0, 70));
                writer.TryWriteChunk(TestData.TerrainChunk(0, cy, 1, 70));
                writer.TryWriteChunk(TestData.TerrainChunk(1, cy, 0, 70));
                writer.TryWriteChunk(TestData.TerrainChunk(1, cy, 1, 70));
            }
            writer.Flush();
        }

        var set = DumpSet.Open(tmp.Path);
        var model = SurfaceModel.Build(set, 0, new BlockBounds(0, 0, 15, 15));

        Assert.Equal(16, model.Width);
        Assert.Equal(16, model.Depth);
        Assert.True(model.Bounds.Contains(15, 15));
        Assert.False(model.Bounds.Contains(16, 0));
    }

    [Fact]
    public void Build_EmptyDump_Throws()
    {
        using var tmp = new TempDir("emptydump");
        TestData.Manifest().Save(tmp.Path);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "dim0"));

        var set = DumpSet.Open(tmp.Path);
        Assert.Throws<InvalidOperationException>(() => SurfaceModel.Build(set, 0));
    }
}
