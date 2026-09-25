using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

public class CaptureFormatTests
{
    private static byte[] Payload(int length, int seed = 1)
    {
        var data = new byte[length];
        var rng = new Random(seed);
        rng.NextBytes(data);
        return data;
    }

    [Fact]
    public void RoundTrip_SmallAndLargePayloads()
    {
        using var tmp = new TempDir("capture");
        var small = Payload(64);
        var large = Payload(200_000, seed: 7);
        var medium = Payload(5000, seed: 3);

        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.ServerIdentification, small);
            writer.Write(CaptureRecordType.Chunk, large);
            writer.Write(CaptureRecordType.MapChunk, medium);
        }

        var records = CaptureReader.Read(tmp.Path).ToList();
        Assert.Equal(3, records.Count);

        Assert.Equal(CaptureRecordType.ServerIdentification, records[0].Type);
        Assert.Equal(small, records[0].Payload);

        Assert.Equal(CaptureRecordType.Chunk, records[1].Type);
        Assert.Equal(large, records[1].Payload);

        Assert.Equal(CaptureRecordType.MapChunk, records[2].Type);
        Assert.Equal(medium, records[2].Payload);
    }

    [Fact]
    public void LargePayload_IsCompressed()
    {
        using var tmp = new TempDir("capture-compress");
        var repetitive = new byte[100_000];
        Array.Fill(repetitive, (byte)7);

        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.Chunk, repetitive);
        }

        long fileSize = new FileInfo(Path.Combine(tmp.Path, CaptureFormat.FileName)).Length;
        Assert.True(fileSize < repetitive.Length / 10,
            $"the compressible payload should shrink, but the file took {fileSize} bytes");

        var record = Assert.Single(CaptureReader.Read(tmp.Path));
        Assert.Equal(repetitive, record.Payload);
    }

    [Fact]
    public void SmallPayload_IsStoredRaw()
    {
        using var tmp = new TempDir("capture-small");
        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.LevelInitialize, new byte[16]);
        }

        long fileSize = new FileInfo(Path.Combine(tmp.Path, CaptureFormat.FileName)).Length;
        Assert.Equal(CaptureFormat.FileHeaderSize + CaptureFormat.RecordHeaderSize + 16, fileSize);
    }

    [Fact]
    public void ReopeningCapture_AppendsInsteadOfTruncating()
    {
        using var tmp = new TempDir("capture-resume");

        using (var w1 = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            w1.Write(CaptureRecordType.Chunk, Payload(100, 1));
        }
        using (var w2 = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            w2.Write(CaptureRecordType.Chunk, Payload(100, 2));
            w2.Write(CaptureRecordType.MapRegion, Payload(100, 3));
        }

        var records = CaptureReader.Read(tmp.Path).ToList();
        Assert.Equal(3, records.Count);
        Assert.Equal(Payload(100, 1), records[0].Payload);
        Assert.Equal(Payload(100, 2), records[1].Payload);
        Assert.Equal(Payload(100, 3), records[2].Payload);
    }

    [Fact]
    public void CorruptedPayload_IsDetectedByCrc()
    {
        using var tmp = new TempDir("capture-corrupt");
        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.Chunk, Payload(5000));
        }

        string file = Path.Combine(tmp.Path, CaptureFormat.FileName);
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        Assert.Throws<InvalidDataException>(() => CaptureReader.Read(tmp.Path).ToList());
    }

    [Fact]
    public void TruncatedLastRecord_StopsCleanlyAndKeepsEarlierRecords()
    {
        using var tmp = new TempDir("capture-truncated");
        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.ServerIdentification, Payload(100, 1));
            writer.Write(CaptureRecordType.Chunk, Payload(50_000, 2));
        }

        // Truncate the file in the middle of the last record — this is what a game crash looks like.
        string file = Path.Combine(tmp.Path, CaptureFormat.FileName);
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..(bytes.Length - 1234)]);

        var records = CaptureReader.Read(tmp.Path).ToList();
        Assert.Single(records);
        Assert.Equal(CaptureRecordType.ServerIdentification, records[0].Type);
    }

    [Fact]
    public void WrongMagic_FailsFast()
    {
        using var tmp = new TempDir("capture-badmagic");
        Directory.CreateDirectory(tmp.Path);
        File.WriteAllBytes(Path.Combine(tmp.Path, CaptureFormat.FileName), new byte[64]);

        Assert.Throws<InvalidDataException>(() => CaptureReader.Read(tmp.Path).ToList());
    }

    [Fact]
    public void MissingFile_YieldsNothing()
    {
        using var tmp = new TempDir("capture-missing");
        Assert.Empty(CaptureReader.Read(tmp.Path));
    }

    [Fact]
    public void FilterByType_ReturnsOnlyRequested()
    {
        using var tmp = new TempDir("capture-filter");
        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.Chunk, Payload(10, 1));
            writer.Write(CaptureRecordType.MapChunk, Payload(10, 2));
            writer.Write(CaptureRecordType.Chunk, Payload(10, 3));
        }

        var chunks = CaptureReader.Read(tmp.Path, CaptureRecordType.Chunk).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(Payload(10, 1), chunks[0].Payload);
        Assert.Equal(Payload(10, 3), chunks[1].Payload);
    }

    [Fact]
    public void Summarize_CountsByType()
    {
        using var tmp = new TempDir("capture-summary");
        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.Chunk, new byte[100]);
            writer.Write(CaptureRecordType.Chunk, new byte[200]);
            writer.Write(CaptureRecordType.MapRegion, new byte[300]);
        }

        var (counts, total) = CaptureReader.Summarize(tmp.Path);
        Assert.Equal(2, counts[CaptureRecordType.Chunk]);
        Assert.Equal(1, counts[CaptureRecordType.MapRegion]);
        Assert.Equal(600, total);
    }

    [Fact]
    public void Manifest_TracksCountsAndRoundTrips()
    {
        using var tmp = new TempDir("capture-manifest");
        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest { GameVersion = "1.22.7" }))
        {
            writer.Write(CaptureRecordType.Chunk, new byte[10]);
            writer.Write(CaptureRecordType.Chunk, new byte[10]);
            writer.Write(CaptureRecordType.MapChunk, new byte[10]);
        }

        var manifest = CaptureManifest.Load(tmp.Path);
        Assert.Equal("1.22.7", manifest.GameVersion);
        Assert.Equal(3, manifest.RecordsWritten);
        Assert.Equal(2, manifest.Counts["Chunk"]);
        Assert.Equal(1, manifest.Counts["MapChunk"]);
    }

    [Fact]
    public void Manifest_MissingFile_Throws()
    {
        using var tmp = new TempDir("capture-nomanifest");
        Assert.Throws<FileNotFoundException>(() => CaptureManifest.Load(tmp.Path));
    }

    [Fact]
    public void OlderFormatVersion_IsRejectedWithClearMessage()
    {
        using var tmp = new TempDir("capture-oldversion");
        Directory.CreateDirectory(tmp.Path);

        // A version 1 file header — this is what a capture taken by an older build looks like.
        var header = new byte[CaptureFormat.FileHeaderSize];
        CaptureFormat.WriteFileHeader(header);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), 1);
        File.WriteAllBytes(Path.Combine(tmp.Path, CaptureFormat.FileName), header);

        var error = Assert.Throws<InvalidDataException>(() => CaptureReader.Read(tmp.Path).ToList());
        Assert.Contains("version 1", error.Message);
    }

    [Fact]
    public void Writer_ArchivesForeignVersionFileInsteadOfMixing()
    {
        using var tmp = new TempDir("capture-archive");
        Directory.CreateDirectory(tmp.Path);

        var header = new byte[CaptureFormat.FileHeaderSize];
        CaptureFormat.WriteFileHeader(header);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), 1);
        string path = Path.Combine(tmp.Path, CaptureFormat.FileName);
        var oldBytes = new byte[CaptureFormat.FileHeaderSize + 4];
        header.CopyTo(oldBytes, 0);
        File.WriteAllBytes(path, oldBytes);

        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.Chunk, new byte[16]);
        }

        // The old file is kept under a versioned name, the new one reads fine.
        Assert.True(File.Exists(Path.Combine(tmp.Path, CaptureFormat.ArchivedFileName(1))),
            "the old capture should be set aside, not overwritten");
        Assert.Single(CaptureReader.Read(tmp.Path));
    }

    [Fact]
    public void Describe_CoversEveryType()
    {
        foreach (CaptureRecordType type in Enum.GetValues<CaptureRecordType>())
        {
            Assert.False(string.IsNullOrWhiteSpace(CaptureFormat.Describe(type)));
        }
    }

    [Fact]
    public void Manifest_ContinuesCountsWhenFileIsReopened()
    {
        using var tmp = new TempDir("capture-manifest-continue");

        // First 'game run': two records, the manifest is saved.
        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.Chunk, new byte[16]);
            writer.Write(CaptureRecordType.MapChunk, new byte[16]);
            writer.SaveManifest();
        }

        // A second run opens the same file: the statistics must not reset.
        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            Assert.Equal(2, writer.Manifest.RecordsWritten);
            Assert.Equal(1, writer.Manifest.Counts["Chunk"]);
            Assert.Equal(1, writer.Manifest.Counts["MapChunk"]);

            writer.Write(CaptureRecordType.Chunk, new byte[16]);
            writer.SaveManifest();
        }

        var manifest = CaptureManifest.Load(tmp.Path);
        Assert.Equal(3, manifest.RecordsWritten);
        Assert.Equal(2, manifest.Counts["Chunk"]);
        Assert.Equal(1, manifest.Counts["MapChunk"]);
        Assert.Equal(3, CaptureReader.Read(tmp.Path).Count());
    }

    [Fact]
    public void Palette_WithIntMinLength_IsRejectedWithoutOverflow()
    {
        // length = -n with n = int.MinValue overflows int; the blob must be
        // rejected, not read as valid.
        var blob = new byte[CombinedLayerBlob.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blob, int.MinValue);

        Assert.False(CombinedLayerBlob.TryReadPalette(blob, codec: null, out _, out _, out _));
    }
}
