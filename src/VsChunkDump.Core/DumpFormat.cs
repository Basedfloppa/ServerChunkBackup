using System.Buffers.Binary;
using System.IO.Compression;

namespace VsChunkDump.Core;

/// <summary>
/// Binary chunk dump format 'vschunkdump'.
///
/// Dump directory layout:
///   &lt;root&gt;/manifest.json          — world metadata and the block table
///   &lt;root&gt;/dim0/r.0.0.vscr        — region of 16x16 chunks (512x512 blocks), all sections along Y
///   &lt;root&gt;/chunkdump.log          — text log (written by the mod)
///
/// Region file:
///   16-byte header: "VSRG" | u16 version | u16 headerSize | i32 regionX | i32 regionZ
///   then a sequence of records (append-only; when coordinates repeat,
///   the reader takes the last record).
///
/// Record (36-byte header + payload):
///   0   u32  magic "VSCR"
///   4   u16  version
///   6   u16  flags            (bit0 = payload is Brotli-compressed)
///   8   i32  chunkX
///   12  i32  chunkY
///   16  i32  chunkZ
///   20  i32  dim
///   24  u32  uncompressedPayloadLen
///   28  u32  payloadLen
///   32  u32  crc32(payload)
///   36  ...  payload
///
/// Payload (before compression):
///   u8  payloadVersion (=1)
///   u8  layerCount (1 or 2; 2 = a liquids layer is present)
///   [ChunkLayer] x layerCount   (see ChunkLayer)
/// </summary>
public static class DumpFormat
{
    public const string ManifestFileName = "manifest.json";
    public const string FormatName = "vschunkdump";
    public const int FormatVersion = 1;

    public const uint RegionMagic = 0x47525356; // "VSRG" in LE
    public const uint RecordMagic = 0x52435356; // "VSCR" in LE
    public const int RegionHeaderSize = 16;
    public const int RecordHeaderSize = 36;

    public const ushort FlagBrotli = 1;

    /// <summary>Path to a region file inside the dump directory.</summary>
    public static string RegionFilePath(string root, int dim, int regionX, int regionZ)
        => Path.Combine(root, "dim" + dim, $"r.{regionX}.{regionZ}.vscr");

    /// <summary>Serialize the payload (block/liquid layers) into bytes.</summary>
    public static byte[] EncodePayload(ReadOnlySpan<int> blocks, int[]? fluids)
    {
        var layerBlocks = ChunkLayer.Encode(blocks);
        ChunkLayer? layerFluids = null;
        if (fluids != null && !IsAllZero(fluids))
        {
            layerFluids = ChunkLayer.Encode(fluids);
        }

        using var ms = new MemoryStream(64 * 1024);
        ms.WriteByte(1); // payloadVersion
        ms.WriteByte((byte)(layerFluids != null ? 2 : 1));
        layerBlocks.WriteTo(ms);
        layerFluids?.WriteTo(ms);
        return ms.ToArray();
    }

    /// <summary>Parse the payload back into blocks and (possibly) liquids.</summary>
    public static void DecodePayload(ReadOnlySpan<byte> payload, out int[] blocks, out int[]? fluids)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        int ver = ms.ReadByte();
        if (ver != 1) throw new InvalidDataException($"Unsupported payload version: {ver}");
        int layerCount = ms.ReadByte();
        if (layerCount is < 1 or > 2) throw new InvalidDataException($"Invalid layer count: {layerCount}");

        var blocksArr = new int[ChunkGeometry.BlocksPerChunk];
        ChunkLayer.ReadFrom(ms).Decode(blocksArr);
        blocks = blocksArr;

        if (layerCount == 2)
        {
            var fluidsArr = new int[ChunkGeometry.BlocksPerChunk];
            ChunkLayer.ReadFrom(ms).Decode(fluidsArr);
            fluids = fluidsArr;
        }
        else
        {
            fluids = null;
        }
    }

    private static bool IsAllZero(ReadOnlySpan<int> v)
    {
        foreach (int x in v) if (x != 0) return false;
        return true;
    }

    public static byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var ms = new MemoryStream(data.Length / 4 + 64);
        using (var br = new BrotliStream(ms, level, leaveOpen: true))
        {
            br.Write(data);
        }
        return ms.ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> data, int uncompressedLength)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var br = new BrotliStream(input, CompressionMode.Decompress);
        var outBuf = new byte[uncompressedLength];
        int read = 0;
        while (read < uncompressedLength)
        {
            int n = br.Read(outBuf, read, uncompressedLength - read);
            if (n <= 0) break;
            read += n;
        }
        return outBuf;
    }

    /// <summary>Write a record header into the buffer.</summary>
    public static void WriteRecordHeader(Span<byte> buffer, ushort flags, int x, int y, int z, int dim,
        uint uncompressedLen, uint payloadLen, uint crc)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[..4], RecordMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..6], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..8], flags);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[8..12], x);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[12..16], y);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[16..20], z);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[20..24], dim);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[24..28], uncompressedLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[28..32], payloadLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[32..36], crc);
    }
}
