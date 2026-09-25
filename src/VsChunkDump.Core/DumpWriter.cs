using System.Buffers.Binary;
using System.IO.Compression;

namespace VsChunkDump.Core;

/// <summary>
/// Writes a chunk dump: region files (append-only) + manifest.json.
/// Thread-safe (internal lock), but designed for a single background writer thread.
/// </summary>
public sealed class DumpWriter : IDisposable
{
    private readonly string _root;
    private readonly DumpManifest _manifest;
    private readonly CompressionLevel _level;
    private readonly object _sync = new();

    private readonly Dictionary<(int Dim, int Rx, int Rz), RegionFile> _regionFiles = [];
    private readonly HashSet<long> _written = [];
    private bool _manifestDirty;
    private bool _disposed;

    public DumpManifest Manifest => _manifest;
    public string Root => _root;
    public int UniqueChunks { get { lock (_sync) return _written.Count; } }

    public DumpWriter(string root, DumpManifest manifest, CompressionLevel level = CompressionLevel.Fastest)
    {
        _root = root;
        _manifest = manifest;
        _level = level;
        Directory.CreateDirectory(root);
        ScanExisting();
    }

    /// <summary>Whether this chunk has already been written (including previous runs).</summary>
    public bool HasChunk(int dim, int x, int y, int z)
    {
        lock (_sync) return _written.Contains(ChunkRecord.PackKey(dim, x, y, z));
    }

    /// <summary>
    /// Write a chunk. Returns false if it was already written before.
    ///
    /// Encoding and compression (the most expensive part) run OUTSIDE the lock,
    /// so that the thread calling <see cref="HasChunk"/> is not blocked
    /// (in the mod that is the game thread).
    /// </summary>
    public bool TryWriteChunk(ChunkRecord chunk)
    {
        byte[] compressed;
        uint payloadLength;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_written.Add(chunk.Key)) return false;
        }

        try
        {
            byte[] payload = DumpFormat.EncodePayload(chunk.Blocks, chunk.Fluids);
            payloadLength = (uint)payload.Length;
            compressed = DumpFormat.Compress(payload, _level);
        }
        catch (Exception)
        {
            lock (_sync) _written.Remove(chunk.Key);
            throw;
        }

        lock (_sync)
        {
            var rf = GetRegionFile(chunk.Dim, ChunkGeometry.RegionOf(chunk.X), ChunkGeometry.RegionOf(chunk.Z));
            Span<byte> header = stackalloc byte[DumpFormat.RecordHeaderSize];
            DumpFormat.WriteRecordHeader(header, DumpFormat.FlagBrotli, chunk.X, chunk.Y, chunk.Z, chunk.Dim,
                payloadLength, (uint)compressed.Length, Crc32.Compute(compressed));
            rf.Stream.Write(header);
            rf.Stream.Write(compressed);

            _manifest.RecordsWritten++;
            _manifest.UniqueChunks = _written.Count;
            _manifestDirty = true;
        }
        return true;
    }

    public void Flush()
    {
        lock (_sync)
        {
            FlushStreams();
            if (_manifestDirty)
            {
                _manifest.Save(_root);
                _manifestDirty = false;
            }
        }
    }

    /// <summary>Flush the region file buffers without touching manifest.json.</summary>
    public void FlushStreams()
    {
        lock (_sync)
        {
            foreach (var rf in _regionFiles.Values)
            {
                rf.Stream.Flush(flushToDisk: false);
            }
        }
    }

    /// <summary>Force-save the manifest.</summary>
    public void SaveManifest()
    {
        lock (_sync)
        {
            _manifest.Save(_root);
            _manifestDirty = false;
        }
    }

    private RegionFile GetRegionFile(int dim, int rx, int rz)
    {
        var key = (dim, rx, rz);
        if (_regionFiles.TryGetValue(key, out var existing)) return existing;

        string path = DumpFormat.RegionFilePath(_root, dim, rx, rz);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        bool fresh = !File.Exists(path) || new FileInfo(path).Length < DumpFormat.RegionHeaderSize;
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (fresh)
        {
            stream.SetLength(0);
            Span<byte> h = stackalloc byte[DumpFormat.RegionHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(h[..4], DumpFormat.RegionMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(h[4..6], DumpFormat.FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(h[6..8], DumpFormat.RegionHeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..12], rx);
            BinaryPrimitives.WriteInt32LittleEndian(h[12..16], rz);
            stream.Write(h);
        }
        stream.Seek(0, SeekOrigin.End);

        var rf = new RegionFile(path, stream);
        _regionFiles[key] = rf;
        // The stream is owned by the map, not by this method: it stays open for
        // appends and is disposed once, in Dispose().
        return rf;
    }

    /// <summary>
    /// On startup, read the regions that already exist so that chunks are not written
    /// again (important when stitching several sessions into one dump).
    /// </summary>
    private void ScanExisting()
    {
        string dimRoot = _root;
        if (!Directory.Exists(dimRoot)) return;
        Span<byte> header = stackalloc byte[DumpFormat.RecordHeaderSize];
        foreach (string dir in Directory.EnumerateDirectories(_root, "dim*"))
        {
            if (!int.TryParse(Path.GetFileName(dir).AsSpan(3), out int dim)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.vscr"))
            {
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length < DumpFormat.RegionHeaderSize) continue;
                    long fileLength = fs.Length;
                    fs.Seek(DumpFormat.RegionHeaderSize, SeekOrigin.Begin);
                    while (true)
                    {
                        if (!TryReadHeader(fs, header)) break;
                        uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);
                        int x = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
                        int y = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
                        int z = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);

                        // A record could be truncated by an abrupt game shutdown:
                        // mark the chunk as written only if its payload is fully on disk,
                        // otherwise it will be rewritten in the next session.
                        long recordEnd = fs.Position + payloadLen;
                        if (recordEnd > fileLength) break;

                        _written.Add(ChunkRecord.PackKey(dim, x, y, z));
                        fs.Seek(payloadLen, SeekOrigin.Current);
                    }
                }
                catch (Exception)
                {
                    // Corrupt or partially written file — simply ignore it while scanning.
                }
            }
        }
    }

    internal static bool TryReadHeader(Stream s, Span<byte> header)
    {
        int read = 0;
        while (read < header.Length)
        {
            int n = s.Read(header[read..]);
            if (n <= 0) return false;
            read += n;
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) == DumpFormat.RecordMagic;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { Flush(); } catch (Exception) { /* on game shutdown the files may already be closed */ }
            foreach (var rf in _regionFiles.Values) rf.Stream.Dispose();
            _regionFiles.Clear();
        }
    }

    private sealed record RegionFile(string Path, FileStream Stream);
}
