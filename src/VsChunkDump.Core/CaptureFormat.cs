using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VsChunkDump.Core;

/// <summary>
/// Record type in a capture file. The payload is the raw bytes of the corresponding game
/// protobuf message (Cito serializer), so the format does not depend on
/// game types and can be read offline.
/// </summary>
public enum CaptureRecordType : ushort
{
    /// <summary>Packet_ServerIdentification: game version, world dimensions, seed, mod list.</summary>
    ServerIdentification = 1,

    /// <summary>Packet_ServerLevelInitialize: chunk/region sizes, view distance.</summary>
    LevelInitialize = 2,

    /// <summary>Packet_WorldMetaData: sea level, light levels, world config.</summary>
    WorldMetaData = 3,

    /// <summary>Packet_ServerAssets: block registry (id -&gt; code) and other assets.</summary>
    ServerAssets = 4,

    /// <summary>Packet_ServerChunk: one chunk with all its fields (blocks, light, light saturation, liquids, decor, block entities, moddata).</summary>
    Chunk = 5,

    /// <summary>Packet_ServerMapChunk: column heightmaps.</summary>
    MapChunk = 6,

    /// <summary>Packet_MapRegion: world region (terrain, climate, ores).</summary>
    MapRegion = 7,

    /// <summary>
    /// Packet_BlockEntities (id 48): block entity updates outside a chunk —
    /// this is how the server sends an interaction rollback.
    /// </summary>
    BlockEntityUpdate = 8
}

/// <summary>
/// Capture file format 'vscapture' v2 — an append-only container of raw packets.
///
/// File header (16 bytes):
///   0  u32 magic "VSCP"
///   4  u16 version
///   6  u16 headerSize
///   8  u32 reserved
///   12 u32 reserved
///
/// Record (20-byte header + payload):
///   0  u32 magic "VSRC"
///   4  u16 recordType
///   6  u16 flags        (bit 0 = payload is Brotli-compressed)
///   8  u32 payloadLen
///   12 u32 rawLen       (length before compression)
///   16 u32 crc32(payload)
///   20 ...              payload
/// </summary>
public static class CaptureFormat
{
    public const string FileName = "capture.vscap";
    public const string ManifestFileName = "capture.json";
    public const string FormatName = "vscapture";
    public const int FormatVersion = 2;

    public const uint FileMagic = 0x50435356;   // "VSCP"
    public const uint RecordMagic = 0x43525356; // "VSRC"
    public const int FileHeaderSize = 16;
    public const int RecordHeaderSize = 20;
    public const ushort FlagBrotli = 1;

    /// <summary>Threshold below which compression is not applied (small records are not worth compressing).</summary>
    public const int CompressionThreshold = 4096;

    public static void WriteFileHeader(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[..4], FileMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..6], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..8], FileHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..12], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..16], 0);
    }

    public static bool IsFileHeader(ReadOnlySpan<byte> buffer)
        => BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]) == FileMagic;

    /// <summary>Format version recorded in the file header.</summary>
    public static int ReadFileVersion(ReadOnlySpan<byte> header)
        => BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]);

    /// <summary>Name of the file into which an incompatible old capture is set aside.</summary>
    public static string ArchivedFileName(int version) => $"capture.v{version}.vscap";

    public static void WriteRecordHeader(Span<byte> buffer, CaptureRecordType type, ushort flags,
        uint payloadLen, uint rawLen, uint crc)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[..4], RecordMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..6], (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..8], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..12], payloadLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..16], rawLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..20], crc);
    }

    public static bool IsRecordHeader(ReadOnlySpan<byte> buffer)
        => BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]) == RecordMagic;

    public static byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var ms = new MemoryStream(data.Length / 4 + 64);
        using (var br = new BrotliStream(ms, level, leaveOpen: true))
        {
            br.Write(data);
        }
        return ms.ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> data, int rawLength)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var br = new BrotliStream(input, CompressionMode.Decompress);
        var buffer = new byte[rawLength];
        int read = 0;
        while (read < rawLength)
        {
            int n = br.Read(buffer, read, rawLength - read);
            if (n <= 0) break;
            read += n;
        }
        return buffer;
    }

    /// <summary>Human-readable record type name (for logs and reports).</summary>
    public static string Describe(CaptureRecordType type) => type switch
    {
        CaptureRecordType.ServerIdentification => "server identification",
        CaptureRecordType.LevelInitialize => "world parameters",
        CaptureRecordType.WorldMetaData => "world metadata",
        CaptureRecordType.ServerAssets => "block registry and assets",
        CaptureRecordType.Chunk => "chunk",
        CaptureRecordType.MapChunk => "column heightmap",
        CaptureRecordType.MapRegion => "world region",
        CaptureRecordType.BlockEntityUpdate => "block entity update",
        _ => "unknown (" + (ushort)type + ")"
    };
}

/// <summary>One capture record: type and decompressed payload.</summary>
public sealed record CaptureRecord(CaptureRecordType Type, byte[] Payload, long Offset);

/// <summary>Capture manifest (&lt;dir&gt;/capture.json).</summary>
public sealed class CaptureManifest
{
    [JsonPropertyName("format")] public string Format { get; set; } = CaptureFormat.FormatName;
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CaptureFormat.FormatVersion;
    [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }
    [JsonPropertyName("modVersion")] public string? ModVersion { get; set; }
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
    [JsonPropertyName("startedUtc")] public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("updatedUtc")] public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("recordsWritten")] public long RecordsWritten { get; set; }
    [JsonPropertyName("counts")] public Dictionary<string, long> Counts { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Bump(CaptureRecordType type)
    {
        RecordsWritten++;
        string key = type.ToString();
        Counts[key] = Counts.GetValueOrDefault(key) + 1;
    }

    public void Save(string root)
    {
        UpdatedUtc = DateTimeOffset.UtcNow;
        string path = Path.Combine(root, CaptureFormat.ManifestFileName);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Continue the statistics of an existing manifest. The capture file is
    /// append-only and survives between game runs, so without this capture.json
    /// would show zeros after a restart even though the file does contain records.
    /// </summary>
    public void ContinueFrom(CaptureManifest? previous)
    {
        if (previous == null) return;
        RecordsWritten = previous.RecordsWritten;
        Counts = new Dictionary<string, long>(previous.Counts);
        if (previous.StartedUtc != default) StartedUtc = previous.StartedUtc;
        if (!string.IsNullOrWhiteSpace(previous.ServerName)) ServerName = previous.ServerName;
    }

    /// <summary>Read the manifest if it exists and is valid. null otherwise (does not throw).</summary>
    public static CaptureManifest? TryLoad(string root)
    {
        try
        {
            string path = Path.Combine(root, CaptureFormat.ManifestFileName);
            if (!File.Exists(path)) return null;
            var manifest = JsonSerializer.Deserialize<CaptureManifest>(File.ReadAllText(path), Options);
            if (manifest == null) return null;
            if (!string.Equals(manifest.Format, CaptureFormat.FormatName, StringComparison.OrdinalIgnoreCase)) return null;
            return manifest;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static CaptureManifest Load(string root)
    {
        string path = Path.Combine(root, CaptureFormat.ManifestFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Capture manifest not found: {path}", path);
        var m = JsonSerializer.Deserialize<CaptureManifest>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("Failed to read capture.json");
        if (!string.Equals(m.Format, CaptureFormat.FormatName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unknown capture format: {m.Format}");
        return m;
    }
}

/// <summary>
/// Writes the capture file. Append-only: a truncated record from an abrupt shutdown
/// is simply ignored by the reader, and the data of previous records stays intact.
/// </summary>
public sealed class CaptureWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly FileStream _stream;
    private readonly CaptureManifest _manifest;
    private readonly CompressionLevel _level;
    private bool _disposed;

    public string Root { get; }
    public CaptureManifest Manifest => _manifest;
    public long RecordsWritten { get { lock (_sync) return _manifest.RecordsWritten; } }

    public CaptureWriter(string root, CaptureManifest manifest,
        CompressionLevel level = CompressionLevel.Fastest)
    {
        Root = root;
        _manifest = manifest;
        _level = level;
        Directory.CreateDirectory(root);

        string path = Path.Combine(root, CaptureFormat.FileName);

        // The record body format has changed, so appending to a file of another
        // version is not allowed — otherwise the result is an unreadable mix. Set the old file aside.
        if (File.Exists(path) && new FileInfo(path).Length >= CaptureFormat.FileHeaderSize)
        {
            ArchiveIfForeignVersion(path);
        }

        bool fresh = !File.Exists(path) || new FileInfo(path).Length < CaptureFormat.FileHeaderSize;
        if (!fresh)
        {
            // Appending to an existing file — take the statistics from the previous
            // manifest, otherwise capture.json shows zeros after a game restart.
            _manifest.ContinueFrom(CaptureManifest.TryLoad(root));
        }
        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (fresh)
        {
            _stream.SetLength(0);
            Span<byte> header = stackalloc byte[CaptureFormat.FileHeaderSize];
            CaptureFormat.WriteFileHeader(header);
            _stream.Write(header);
        }
        _stream.Seek(0, SeekOrigin.End);
    }

    /// <summary>
    /// If the file header is of a different version, rename it to
    /// <c>capture.vN.vscap</c> and start a new one. The data is not deleted.
    /// </summary>
    private static void ArchiveIfForeignVersion(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[CaptureFormat.FileHeaderSize];
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (probe.Read(header) < CaptureFormat.FileHeaderSize) return;
            }
            if (!CaptureFormat.IsFileHeader(header)) return;

            int version = CaptureFormat.ReadFileVersion(header);
            if (version == CaptureFormat.FormatVersion) return;

            string directory = Path.GetDirectoryName(path)!;
            string archived = Path.Combine(directory, CaptureFormat.ArchivedFileName(version));
            int counter = 1;
            while (File.Exists(archived))
            {
                archived = Path.Combine(directory, $"capture.v{version}-{counter++}.vscap");
            }
            File.Move(path, archived);
        }
        catch (Exception)
        {
            // Failed to set it aside — just start a new file over it.
            try { File.Delete(path); } catch (Exception) { /* nothing we can do */ }
        }
    }

    /// <summary>Append a record. The body is compressed if it is large enough.</summary>
    public void Write(CaptureRecordType type, ReadOnlySpan<byte> payload)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            byte[] body;
            ushort flags = 0;
            if (payload.Length >= CaptureFormat.CompressionThreshold)
            {
                byte[] compressed = CaptureFormat.Compress(payload, _level);
                if (compressed.Length < payload.Length)
                {
                    body = compressed;
                    flags |= CaptureFormat.FlagBrotli;
                }
                else
                {
                    body = payload.ToArray();
                }
            }
            else
            {
                body = payload.ToArray();
            }

            Span<byte> header = stackalloc byte[CaptureFormat.RecordHeaderSize];
            CaptureFormat.WriteRecordHeader(header, type, flags,
                (uint)body.Length, (uint)payload.Length, Crc32.Compute(body));
            _stream.Write(header);
            _stream.Write(body);

            _manifest.Bump(type);
        }
    }

    public void FlushStream()
    {
        lock (_sync) _stream.Flush(flushToDisk: false);
    }

    public void SaveManifest()
    {
        lock (_sync) _manifest.Save(Root);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream.Flush(); } catch (Exception) { /* the game may already be closing files */ }
            _stream.Dispose();
            try { _manifest.Save(Root); } catch (Exception) { /* see above */ }
        }
    }
}

/// <summary>Streaming capture reader. Corrupt/truncated records stop reading without breaking the parse.</summary>
public static class CaptureReader
{
    /// <summary>Read all capture records in order.</summary>
    public static IEnumerable<CaptureRecord> Read(string root)
    {
        string path = Path.Combine(root, CaptureFormat.FileName);
        if (!File.Exists(path)) yield break;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < CaptureFormat.FileHeaderSize) yield break;

        var header = new byte[CaptureFormat.RecordHeaderSize];
        Span<byte> fileHeader = stackalloc byte[CaptureFormat.FileHeaderSize];
        ReadExactly(fs, fileHeader);
        if (!CaptureFormat.IsFileHeader(fileHeader))
            throw new InvalidDataException($"Not a capture file: {path}");

        int version = CaptureFormat.ReadFileVersion(fileHeader);
        if (version != CaptureFormat.FormatVersion)
        {
            throw new InvalidDataException(
                $"Capture file is version {version}, but this build reads version {CaptureFormat.FormatVersion}. "
                + "A capture produced by a different mod version is incompatible: make a new one.");
        }

        long fileLength = fs.Length;
        while (true)
        {
            long offset = fs.Position;
            if (offset + CaptureFormat.RecordHeaderSize > fileLength) yield break;
            if (!TryReadExactly(fs, header)) yield break;
            if (!CaptureFormat.IsRecordHeader(header)) yield break;

            var type = (CaptureRecordType)BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]);
            uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
            uint rawLen = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);

            if (offset + CaptureFormat.RecordHeaderSize + payloadLen > fileLength) yield break;
            var body = new byte[payloadLen];
            if (!TryReadExactly(fs, body)) yield break;
            if (Crc32.Compute(body) != crc)
                throw new InvalidDataException($"CRC mismatch in {path} @ {offset}");

            byte[] payload = (flags & CaptureFormat.FlagBrotli) != 0
                ? CaptureFormat.Decompress(body, (int)rawLen)
                : body;

            yield return new CaptureRecord(type, payload, offset);
        }
    }

    /// <summary>Read only records of the given type.</summary>
    public static IEnumerable<CaptureRecord> Read(string root, CaptureRecordType type)
    {
        foreach (var record in Read(root))
        {
            if (record.Type == type) yield return record;
        }
    }

    /// <summary>Capture file summary: how many records of each type and how many bytes.</summary>
    public static (Dictionary<CaptureRecordType, long> Counts, long TotalBytes) Summarize(string root)
    {
        var counts = new Dictionary<CaptureRecordType, long>();
        long total = 0;
        foreach (var record in Read(root))
        {
            counts[record.Type] = counts.GetValueOrDefault(record.Type) + 1;
            total += record.Payload.Length;
        }
        return (counts, total);
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer[read..]);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of capture file");
            read += n;
        }
    }

    private static bool TryReadExactly(Stream s, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
