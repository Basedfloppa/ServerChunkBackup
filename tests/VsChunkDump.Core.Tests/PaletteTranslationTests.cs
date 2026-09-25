using System.Buffers.Binary;
using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

/// <summary>
/// Tests for id translation in a blob palette. The game's zstd is not needed: instead
/// a stub codec is plugged in that only copies bytes — the blob layout does
/// not change because of it, and that layout is exactly what is verified here.
/// </summary>
public class PaletteTranslationTests
{
    /// <summary>'Compression' without compression: lets us verify how palette frames are parsed.</summary>
    private sealed class CopyCodec : IZstdCodec
    {
        public byte[] Decompress(byte[] data, int offset, int length)
        {
            var result = new byte[length];
            Array.Copy(data, offset, result, 0, length);
            return result;
        }

        public byte[] Compress(byte[] data, int length)
        {
            var result = new byte[length];
            Array.Copy(data, result, length);
            return result;
        }
    }

    private static byte[] RawBlob(int[] palette, params byte[] bitPlanes)
    {
        var blob = new byte[4 + palette.Length * 4 + bitPlanes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(0, 4), -(palette.Length * 4));
        for (int i = 0; i < palette.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4 + i * 4, 4), palette[i]);
        bitPlanes.CopyTo(blob, 4 + palette.Length * 4);
        return blob;
    }

    private static byte[] CompressedBlob(int[] palette, IZstdCodec codec, params byte[] bitPlanes)
    {
        var raw = new byte[palette.Length * 4];
        for (int i = 0; i < palette.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(i * 4, 4), palette[i]);
        byte[] packed = codec.Compress(raw, raw.Length);

        var blob = new byte[4 + packed.Length + bitPlanes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(0, 4), packed.Length);
        packed.CopyTo(blob, 4);
        bitPlanes.CopyTo(blob, 4 + packed.Length);
        return blob;
    }

    private static int[] ReadPalette(byte[] blob, IZstdCodec codec)
    {
        Assert.True(CombinedLayerBlob.TryReadPalette(blob, codec, out var palette, out _, out _));
        return palette!;
    }

    private static void AssertBitPlanesPreserved(byte[] blob, int offset, byte[] expected)
    {
        var actual = blob.AsSpan(offset).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RawPaletteIsRewrittenWithoutTouchingBitPlanes()
    {
        var bitPlanes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] blob = RawBlob([0, 5, 7, 9], bitPlanes);

        bool translated = CombinedLayerBlob.TryTranslatePalette(
            blob, new Dictionary<int, int> { [5] = 2, [7] = 3, [9] = 4 }, 0, null, out var result, out var stats);

        Assert.True(translated);
        Assert.Equal(blob.Length, result.Length);
        Assert.Equal([0, 2, 3, 4], ReadPalette(result, new CopyCodec()));
        AssertBitPlanesPreserved(result, 4 + 4 * 4, bitPlanes);
        Assert.Equal(4, stats.Entries);
        Assert.Equal(3, stats.Changed);
        Assert.Equal(0, stats.ReplacedByFallback);
        Assert.False(stats.WasCompressed);

        // Do not corrupt the source blob.
        Assert.Equal([0, 5, 7, 9], ReadPalette(blob, new CopyCodec()));
    }

    [Fact]
    public void IdentityMapLeavesBlobAlone()
    {
        byte[] blob = RawBlob([0, 5, 7], 0xAA, 0xBB);

        bool translated = CombinedLayerBlob.TryTranslatePalette(
            blob, new Dictionary<int, int> { [5] = 5, [7] = 7 }, 0, null, out var result, out _);

        Assert.False(translated);
        Assert.Same(blob, result);
    }

    [Fact]
    public void CompressedPaletteIsRebuiltAndBitPlanesSurvive()
    {
        var codec = new CopyCodec();
        var bitPlanes = new byte[] { 9, 8, 7, 6, 5 };
        byte[] blob = CompressedBlob([0, 100, 200, 300, 400], codec, bitPlanes);

        bool translated = CombinedLayerBlob.TryTranslatePalette(
            blob, new Dictionary<int, int> { [100] = 11, [200] = 12, [300] = 13, [400] = 14 }, 0,
            codec, out var result, out var stats);

        Assert.True(translated);
        Assert.True(stats.WasCompressed);
        Assert.Equal(4, stats.Changed);
        Assert.Equal([0, 11, 12, 13, 14], ReadPalette(result, codec));

        // The bit planes start right after the palette, and the palette length may have
        // changed — so the header must point at the new offset.
        int n = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(0, 4));
        Assert.Equal(n, result.Length - 4 - bitPlanes.Length);
        AssertBitPlanesPreserved(result, 4 + n, bitPlanes);
    }

    [Fact]
    public void MissingIdsFallBackAndAreCounted()
    {
        byte[] blob = RawBlob([0, 42, 43, 44], 0x01);

        bool translated = CombinedLayerBlob.TryTranslatePalette(
            blob, new Dictionary<int, int> { [42] = 7 }, 0, null, out var result, out var stats);

        Assert.True(translated);
        Assert.Equal([0, 7, 0, 0], ReadPalette(result, new CopyCodec()));
        Assert.Equal(3, stats.Changed);
        Assert.Equal(2, stats.ReplacedByFallback);
    }

    [Fact]
    public void ReadLengthCodeDescribesTheFraming()
    {
        Assert.Equal(-16, CombinedLayerBlob.ReadLengthCode(RawBlob([0, 1, 2, 3])));
        Assert.Equal(0, CombinedLayerBlob.ReadLengthCode([0, 0, 0, 0]));
        Assert.Equal(0, CombinedLayerBlob.ReadLengthCode(new byte[] { 1, 2 }));
        Assert.Equal(0, CombinedLayerBlob.ReadLengthCode(null));

        var codec = new CopyCodec();
        byte[] packed = CompressedBlob([0, 1, 2, 3], codec);
        Assert.Equal(16, CombinedLayerBlob.ReadLengthCode(packed));
    }

    [Fact]
    public void EmptyLayerIsNotTouched()
    {
        byte[] blob = [0, 0, 0, 0];
        Assert.False(CombinedLayerBlob.TryTranslatePalette(
            blob, new Dictionary<int, int> { [1] = 2 }, 0, null, out var result, out _));
        Assert.Same(blob, result);
    }

    [Fact]
    public void SingleEntryPaletteIsSkipped()
    {
        // The game treats count == 1 as an empty layer — there is nothing to translate.
        byte[] blob = RawBlob([77], 0x11, 0x22);
        Assert.False(CombinedLayerBlob.TryTranslatePalette(
            blob, new Dictionary<int, int> { [77] = 5 }, 0, null, out _, out _));
    }

    [Fact]
    public void CompressedPaletteWithoutCodecIsNotTouched()
    {
        var codec = new CopyCodec();
        byte[] blob = CompressedBlob([0, 5, 6], codec, 0x33);

        Assert.False(CombinedLayerBlob.TryTranslatePalette(
            blob, new Dictionary<int, int> { [5] = 1, [6] = 2 }, 0, null, out var result, out _));
        Assert.Same(blob, result);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2 })]
    [InlineData(new byte[] { 5, 0, 0, 0 })]              // N=5, but there is no palette
    [InlineData(new byte[] { 0xFB, 0xFF, 0xFF, 0xFF })]  // N=-5, not a multiple of 4
    [InlineData(new byte[] { 0xF8, 0xFF, 0xFF, 0xFF })]  // N=-8, no palette in the blob
    public void MalformedBlobsAreLeftAlone(byte[] blob)
    {
        bool translated = CombinedLayerBlob.TryTranslatePalette(
            blob, new Dictionary<int, int> { [1] = 2 }, 0, new CopyCodec(), out var result, out _);
        Assert.False(translated);
        Assert.Same(blob, result);
    }
}

/// <summary>Building the 'server id → local id' translation by block code.</summary>
public class BlockIdMapBuilderTests
{
    [Fact]
    public void MatchesByIdsCodeNotById()
    {
        var server = new Dictionary<int, string>
        {
            [0] = "game:air",
            [1] = "game:mantle",
            [2] = "multiblock-monolithic-n2-n2-n2",
            [3] = "game:rock-granite"
        };
        var local = new Dictionary<int, string>
        {
            [0] = "game:air",
            [1] = "game:mantle",
            [2] = "game:meta-filler",
            [3] = "game:meta-pathway",
            [17] = "multiblock-monolithic-n2-n2-n2",
            [23] = "game:rock-granite"
        };

        var result = BlockIdMapBuilder.Build(server, local);

        Assert.Equal(17, result.Map[2]);
        Assert.Equal(23, result.Map[3]);
        Assert.Equal(2, result.Same);
        Assert.Equal(2, result.Moved);
        Assert.Equal(0, result.Missing);
        Assert.False(result.IsIdentity);
    }

    [Fact]
    public void MatchesCodesWrittenWithAndWithoutDomain()
    {
        // The server assets packet and the BlockIDs table in a save write game blocks
        // in short form ('air'), while the archiver dump uses a domain ('game:air').
        var server = new Dictionary<int, string>
        {
            [0] = "air",
            [1] = "mantle",
            [2] = "somemod:thing"
        };
        var local = new Dictionary<int, string>
        {
            [0] = "game:air",
            [7] = "game:mantle",
            [9] = "somemod:thing"
        };

        var result = BlockIdMapBuilder.Build(server, local);

        Assert.Equal(0, result.Map[0]);
        Assert.Equal(7, result.Map[1]);
        Assert.Equal(9, result.Map[2]);
        Assert.Equal(0, result.Missing);
        Assert.Equal(2, result.Moved);
    }

    [Fact]
    public void MissingCodesGoToFallbackAir()
    {
        var server = new Dictionary<int, string> { [0] = "game:air", [5] = "somemod:gone" };
        var local = new Dictionary<int, string> { [0] = "game:air" };

        var result = BlockIdMapBuilder.Build(server, local);

        Assert.Equal(0, result.Map[5]);
        Assert.Equal(1, result.Missing);
        Assert.Contains("somemod:gone", result.MissingSamples);
        Assert.False(result.IsIdentity); // the block exists on the server, but not here
        Assert.Equal(2, result.Map.Count);
    }

    [Fact]
    public void IdenticalRegistriesProduceIdentity()
    {
        var registry = new Dictionary<int, string> { [0] = "game:air", [1] = "game:stone", [2] = "game:soil" };
        var result = BlockIdMapBuilder.Build(registry, registry);
        Assert.True(result.IsIdentity);
        Assert.Equal(3, result.Same);
    }

    [Fact]
    public void DuplicateCodesInLocalRegistryAreDeterministicAndCounted()
    {
        var server = new Dictionary<int, string> { [1] = "game:stone", [2] = "game:stone" };
        var local = new Dictionary<int, string> { [5] = "game:stone", [9] = "game:stone" };

        var result = BlockIdMapBuilder.Build(server, local);

        Assert.Equal(5, result.Map[1]); // the smallest id wins
        Assert.Equal(5, result.Map[2]);
        Assert.Equal(2, result.Moved);
        Assert.Equal(1, result.Collapsed);
    }

    [Fact]
    public void FallbackIsConfigurable()
    {
        var result = BlockIdMapBuilder.Build(
            new Dictionary<int, string> { [4] = "gone:block" },
            new Dictionary<int, string> { [3] = "game:stone" },
            fallbackId: 3);
        Assert.Equal(3, result.Map[4]);
        Assert.Equal(3, result.FallbackId);
    }
}

/// <summary>Portable block registry (JSON).</summary>
public class BlockRegistryTableTests{
    [Fact]
    public void RoundTripsThroughJson()
    {
        using var dir = new TempDir("registry");
        string path = Path.Combine(dir.Path, "local.json");

        var table = BlockRegistryTable.FromDictionary(new Dictionary<int, string>
        {
            [0] = "game:air",
            [1] = "game:mantle",
            [2] = "game:meta-filler"
        });
        table.GameVersion = "1.22.7";
        table.Save(path, "unit-test");

        var loaded = BlockRegistryTable.Load(path);
        Assert.Equal(3, loaded.Count);
        Assert.Equal("game:air", loaded.Blocks[0]);
        Assert.Equal("game:meta-filler", loaded.Blocks[2]);
        Assert.Equal("1.22.7", loaded.GameVersion);
        Assert.Equal("unit-test", loaded.Source);
        Assert.True(loaded.IsDense);
    }

    [Fact]
    public void AcceptsArrayForm()
    {
        using var dir = new TempDir("registry-array");
        string path = Path.Combine(dir.Path, "array.json");
        File.WriteAllText(path, """{"blocks":[[0,"game:air"],[4,"game:stone"]]}""");

        var table = BlockRegistryTable.Load(path);
        Assert.Equal(2, table.Count);
        Assert.Equal("game:stone", table.Blocks[4]);
        Assert.False(table.IsDense); // there is a gap (ids 1..3)
        Assert.Equal(4, table.MaxId);
    }

    [Fact]
    public void AcceptsBareDictionary()
    {
        using var dir = new TempDir("registry-bare");
        string path = Path.Combine(dir.Path, "bare.json");
        File.WriteAllText(path, """{"0":"game:air","1":"game:mantle"}""");

        var table = BlockRegistryTable.Load(path);
        Assert.Equal(2, table.Count);
        Assert.True(table.IsDense);
    }

    [Fact]
    public void RejectsForeignFormat()
    {
        using var dir = new TempDir("registry-foreign");
        string path = Path.Combine(dir.Path, "other.json");
        File.WriteAllText(path, """{"format":"something-else","blocks":{"0":"game:air"}}""");

        var error = Assert.Throws<InvalidDataException>(() => BlockRegistryTable.Load(path));
        Assert.Contains("vsblockregistry", error.Message);
    }

    [Fact]
    public void RejectsEmptyRegistry()
    {
        using var dir = new TempDir("registry-empty");
        string path = Path.Combine(dir.Path, "empty.json");
        File.WriteAllText(path, """{"format":"vsblockregistry","version":1,"blocks":{}}""");

        Assert.Throws<InvalidDataException>(() => BlockRegistryTable.Load(path));
    }

    [Fact]
    public void RejectsFutureVersion()
    {
        using var dir = new TempDir("registry-future");
        string path = Path.Combine(dir.Path, "future.json");
        File.WriteAllText(path, """{"format":"vsblockregistry","version":99,"blocks":{"0":"game:air"}}""");

        var error = Assert.Throws<InvalidDataException>(() => BlockRegistryTable.Load(path));
        Assert.Contains("99", error.Message);
    }
}
