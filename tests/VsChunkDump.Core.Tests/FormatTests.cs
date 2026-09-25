using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

public class ChunkLayerTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(256, 8)]
    [InlineData(257, 9)]
    [InlineData(32768, 15)]
    public void BitsFor_MatchesExpected(int count, int expected)
        => Assert.Equal(expected, ChunkLayer.BitsFor(count));

    [Fact]
    public void RoundTrip_UniformLayer_UsesZeroBits()
    {
        var values = new int[ChunkGeometry.BlocksPerChunk];
        Array.Fill(values, 7);

        var layer = ChunkLayer.Encode(values);

        Assert.Equal(0, layer.Bits);
        Assert.Single(layer.Palette);
        Assert.Empty(layer.Packed);
        Assert.True(layer.EncodedSize < 16, "a uniform layer should take just a few bytes");

        var back = new int[ChunkGeometry.BlocksPerChunk];
        layer.Decode(back);
        Assert.Equal(values, back);
    }

    [Fact]
    public void RoundTrip_TwoValues()
    {
        var rng = new Random(1234);
        var values = new int[ChunkGeometry.BlocksPerChunk];
        for (int i = 0; i < values.Length; i++) values[i] = rng.Next(2) == 0 ? 0 : 99;

        var layer = ChunkLayer.Encode(values);
        Assert.Equal(1, layer.Bits);

        var back = new int[ChunkGeometry.BlocksPerChunk];
        layer.Decode(back);
        Assert.Equal(values, back);
    }

    [Fact]
    public void RoundTrip_MaximumPalette()
    {
        // 32768 distinct values => a 15-bit palette, the maximum case.
        var values = new int[ChunkGeometry.BlocksPerChunk];
        for (int i = 0; i < values.Length; i++) values[i] = i + 1;

        var layer = ChunkLayer.Encode(values);
        Assert.Equal(15, layer.Bits);
        Assert.Equal(ChunkGeometry.BlocksPerChunk, layer.Palette.Length);

        var back = new int[ChunkGeometry.BlocksPerChunk];
        layer.Decode(back);
        Assert.Equal(values, back);
    }

    [Fact]
    public void RoundTrip_ThroughStream()
    {
        var rng = new Random(99);
        var values = new int[ChunkGeometry.BlocksPerChunk];
        for (int i = 0; i < values.Length; i++) values[i] = rng.Next(500);

        using var ms = new MemoryStream();
        ChunkLayer.Encode(values).WriteTo(ms);
        ms.Position = 0;
        var layer = ChunkLayer.ReadFrom(ms);

        var back = new int[ChunkGeometry.BlocksPerChunk];
        layer.Decode(back);
        Assert.Equal(values, back);
    }

    [Fact]
    public void BitsZero_WithEmptyPalette_DoesNotThrow()
    {
        var layer = new ChunkLayer { Bits = 0, Palette = [], Packed = [] };
        var dest = new int[16];
        layer.Decode(dest);
        Assert.All(dest, v => Assert.Equal(0, v));
    }
}

public class PayloadTests
{
    [Fact]
    public void Payload_RoundTrip_BlocksOnly()
    {
        var blocks = TestData.PatternChunk(0, 0, 0, (x, y, z) => (x + y + z) % 5);
        var payload = DumpFormat.EncodePayload(blocks, null);
        DumpFormat.DecodePayload(payload, out var backBlocks, out var backFluids);

        Assert.Equal(blocks, backBlocks);
        Assert.Null(backFluids);
    }

    [Fact]
    public void Payload_RoundTrip_WithFluids()
    {
        var blocks = TestData.PatternChunk(0, 0, 0, (x, y, z) => (y < 10) ? 1 : 0);
        var fluids = TestData.PatternChunk(0, 0, 0, (x, y, z) => (y is >= 10 and < 12) ? 4 : 0);

        var payload = DumpFormat.EncodePayload(blocks, fluids);
        DumpFormat.DecodePayload(payload, out var backBlocks, out var backFluids);

        Assert.Equal(blocks, backBlocks);
        Assert.NotNull(backFluids);
        Assert.Equal(fluids, backFluids);
    }

    [Fact]
    public void Payload_AllZeroFluids_OmitLayer()
    {
        var blocks = TestData.PatternChunk(0, 0, 0, (_, y, _) => y);
        var fluids = new int[ChunkGeometry.BlocksPerChunk];

        var payload = DumpFormat.EncodePayload(blocks, fluids);
        DumpFormat.DecodePayload(payload, out _, out var backFluids);

        Assert.Null(backFluids);
        Assert.Equal(1, payload[1]); // layerCount == 1
    }

    [Fact]
    public void Payload_CompressDecompress_RoundTrip()
    {
        var blocks = TestData.PatternChunk(0, 0, 0, (x, y, z) => (x * 31 + y * 7 + z) % 300);
        var payload = DumpFormat.EncodePayload(blocks, null);
        var compressed = DumpFormat.Compress(payload);
        var restored = DumpFormat.Decompress(compressed, payload.Length);

        Assert.Equal(payload, restored);
        Assert.True(compressed.Length < payload.Length, "Brotli should compress the packed chunk");
    }

    [Fact]
    public void Payload_RejectsUnknownVersion()
    {
        var bad = new byte[] { 99, 1, 0, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() => DumpFormat.DecodePayload(bad, out _, out _));
    }
}

public class Crc32Tests
{
    [Fact]
    public void KnownVector()
        => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void EmptyInput()
        => Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void IncrementalMatchesOneShot()
    {
        var data = "The quick brown fox jumps over the lazy dog"u8;
        uint split = Crc32.Compute(data[..10]);
        uint full = Crc32.Compute(split, data[10..]);
        Assert.Equal(Crc32.Compute(data), full);
    }
}

public class ChunkGeometryTests
{
    [Fact]
    public void Index3d_RoundTrips()
    {
        for (int y = 0; y < 32; y += 7)
        for (int z = 0; z < 32; z += 5)
        for (int x = 0; x < 32; x += 3)
        {
            int index = ChunkGeometry.Index3d(x, y, z);
            Assert.InRange(index, 0, ChunkGeometry.BlocksPerChunk - 1);
            var (ux, uy, uz) = ChunkGeometry.UnIndex3d(index);
            Assert.Equal((x, y, z), (ux, uy, uz));
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 0)]
    [InlineData(16, 1)]
    [InlineData(-1, -1)]
    [InlineData(-16, -1)]
    [InlineData(-17, -2)]
    public void RegionOf_HandlesNegatives(int chunkCoord, int expected)
        => Assert.Equal(expected, ChunkGeometry.RegionOf(chunkCoord));

    [Fact]
    public void FloorMod_IsAlwaysNonNegative()
    {
        for (int i = -40; i < 40; i++)
            Assert.InRange(ChunkGeometry.FloorMod(i, 16), 0, 15);
    }

    [Fact]
    public void UnpackChunkIndex_MatchesDocumentedFormula()
    {
        int cx = 3, cy = 5, cz = 7;
        int mapX = 64, mapZ = 64;
        long index = (long)cx + (long)cz * mapX + (long)cy * mapX * mapZ;

        var (x, y, z) = ChunkGeometry.UnpackChunkIndex(index, mapX, mapZ);
        Assert.Equal((cx, cy, cz), (x, y, z));
    }
}
