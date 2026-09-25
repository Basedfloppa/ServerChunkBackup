using System.Buffers.Binary;

namespace VsChunkDump.Core;

/// <summary>
/// Reading a chunk dump. Chunks are streamed; within a single region
/// the 'last record wins' rule applies (important for append-only
/// files, where a chunk may appear several times).
/// </summary>
public sealed class DumpSet
{
    public string Root { get; }
    public DumpManifest Manifest { get; }
    public BlockTable Blocks { get; }

    private DumpSet(string root, DumpManifest manifest)
    {
        Root = root;
        Manifest = manifest;
        Blocks = new BlockTable(manifest.BlockCodes);
    }

    public static DumpSet Open(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Dump directory not found: {root}");
        var manifest = DumpManifest.Load(root);
        return new DumpSet(root, manifest);
    }

    /// <summary>Dimension directories found in the dump.</summary>
    public IEnumerable<int> Dimensions()
    {
        foreach (string dir in Directory.EnumerateDirectories(Root, "dim*").OrderBy(d => d))
        {
            if (int.TryParse(Path.GetFileName(dir).AsSpan(3), out int dim)) yield return dim;
        }
    }

    /// <summary>All chunks of all dimensions (streamed, region by region).</summary>
    public IEnumerable<ChunkRecord> ReadChunks()
    {
        foreach (int dim in Dimensions())
        {
            foreach (var chunk in ReadChunks(dim)) yield return chunk;
        }
    }

    /// <summary>All chunks of one dimension (streamed, region by region).</summary>
    public IEnumerable<ChunkRecord> ReadChunks(int dim)
    {
        string dir = Path.Combine(Root, "dim" + dim);
        if (!Directory.Exists(dir)) yield break;

        foreach (string file in Directory.EnumerateFiles(dir, "*.vscr").OrderBy(f => f))
        {
            foreach (var chunk in ReadRegionFile(file, dim))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// Quickly (from record headers only, without decompression) enumerate the chunk
    /// coordinates of a dimension. Needed to know the area bounds for rendering in advance.
    /// </summary>
    public IEnumerable<(int X, int Y, int Z)> ChunkCoords(int dim)
    {
        string dir = Path.Combine(Root, "dim" + dim);
        if (!Directory.Exists(dir)) yield break;

        var header = new byte[DumpFormat.RecordHeaderSize];
        foreach (string file in Directory.EnumerateFiles(dir, "*.vscr").OrderBy(f => f))
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < DumpFormat.RegionHeaderSize) continue;
            fs.Seek(DumpFormat.RegionHeaderSize, SeekOrigin.Begin);
            while (true)
            {
                if (!DumpWriter.TryReadHeader(fs, header)) break;
                uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);
                int x = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
                int y = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
                int z = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
                yield return (x, y, z);
                fs.Seek(payloadLen, SeekOrigin.Current);
            }
        }
    }

    /// <summary>Read one region: index the records first, then read only the current ones.</summary>
    public IEnumerable<ChunkRecord> ReadRegionFile(string path, int dim)
    {
        var index = new Dictionary<long, RecordPointer>(2048);
        var header = new byte[DumpFormat.RecordHeaderSize];

        long dataStart;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length < DumpFormat.RegionHeaderSize) yield break;
            var rh = new byte[DumpFormat.RegionHeaderSize];
            ChunkLayer.ReadExactly(fs, rh);
            if (BinaryPrimitives.ReadUInt32LittleEndian(rh[..4]) != DumpFormat.RegionMagic)
                throw new InvalidDataException($"Not a region file: {path}");
            dataStart = DumpFormat.RegionHeaderSize;

            long pos = dataStart;
            while (true)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                if (!DumpWriter.TryReadHeader(fs, header)) break;

                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]);
                int x = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
                int y = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
                int z = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
                uint uncompressedLen = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
                uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[32..36]);

                long key = ChunkRecord.PackKey(dim, x, y, z);
                index[key] = new RecordPointer(pos, payloadLen, uncompressedLen, flags, crc);
                pos += DumpFormat.RecordHeaderSize + payloadLen;
            }
        }

        // Stable order: by Z, then Y, then X (handy for top-down rendering).
        foreach (var kv in index.OrderBy(k => (uint)(k.Key & 0xFFFF)).ThenBy(k => (uint)((k.Key >> 16) & 0xFFFF)).ThenBy(k => (uint)((k.Key >> 32) & 0xFFFF)))
        {
            var p = kv.Value;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(p.Offset + DumpFormat.RecordHeaderSize, SeekOrigin.Begin);
            var payload = new byte[p.PayloadLen];
            ChunkLayer.ReadExactly(fs, payload);
            if (p.Crc != 0 && Crc32.Compute(payload) != p.Crc)
                throw new InvalidDataException($"CRC mismatch in {path} @ {p.Offset}");

            byte[] raw = (p.Flags & DumpFormat.FlagBrotli) != 0
                ? DumpFormat.Decompress(payload, (int)p.UncompressedLen)
                : payload;

            DumpFormat.DecodePayload(raw, out var blocks, out var fluids);
            long k = kv.Key;
            yield return new ChunkRecord
            {
                Dim = (int)((k >> 48) & 0xFFFF),
                X = (int)(short)((k >> 32) & 0xFFFF),
                Y = (int)(short)((k >> 16) & 0xFFFF),
                Z = (int)(short)(k & 0xFFFF),
                Blocks = blocks,
                Fluids = fluids
            };
        }
    }

    /// <summary>Read a specific chunk if it is present in the dump.</summary>
    public bool TryGetChunk(int dim, int x, int y, int z, out ChunkRecord chunk)
    {
        chunk = null!;
        int rx = ChunkGeometry.RegionOf(x);
        int rz = ChunkGeometry.RegionOf(z);
        string path = DumpFormat.RegionFilePath(Root, dim, rx, rz);
        if (!File.Exists(path)) return false;

        foreach (var c in ReadRegionFile(path, dim))
        {
            if (c.X == x && c.Y == y && c.Z == z)
            {
                chunk = c;
                return true;
            }
        }
        return false;
    }

    private readonly record struct RecordPointer(long Offset, uint PayloadLen, uint UncompressedLen, ushort Flags, uint Crc);
}
