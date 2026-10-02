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
    BlockEntityUpdate = 8,

    /// <summary>
    /// One entity (mob, dropped item, item frame) in the form the savegame stores it.
    /// Entities have no packet of their own to capture: the client receives them as
    /// "sync" data, from which the savegame form cannot be assembled. The mod takes
    /// it from the live entity instead (see EntitySaveData).
    /// </summary>
    Entity = 9,

    /// <summary>An entity gone for good (died, burned up, picked up, expired, removed).</summary>
    EntityDespawn = 10,

    /// <summary>
    /// The state of the world at the moment of capture: the game clock and where the
    /// player stood. The clock is what the game stores in
    /// <c>SaveGame.TotalGameSeconds</c>, and both the time of day and the season are
    /// derived from it; the position becomes <c>SaveGame.DefaultSpawn</c>. Without this
    /// record the assembled world opens at its own midnight, in its first spring, and
    /// the player has to <c>/time set</c> and <c>/tp</c> to reach the captured place.
    /// </summary>
    WorldState = 11
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

    /// <summary>
    /// Largest record body accepted. No packet the game sends comes close, so a longer
    /// length is a damaged header — and reading one would try to allocate it.
    /// </summary>
    public const uint MaxRecordBytes = 256u * 1024 * 1024;

    /// <summary>How much of the tail is examined when looking for the last complete record.</summary>
    public const int TailScanBytes = 64 * 1024 * 1024;

    /// <summary>Read exactly the buffer, or report that the stream ended first.</summary>
    internal static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

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
        CaptureRecordType.Entity => "entity",
        CaptureRecordType.EntityDespawn => "entity despawn",
        CaptureRecordType.WorldState => "world state (clock and player)",
        _ => "unknown (" + (ushort)type + ")"
    };
}

/// <summary>One capture record: type and decompressed payload.</summary>
public sealed record CaptureRecord(CaptureRecordType Type, byte[] Payload, long Offset);

/// <summary>
/// A gap in the capture file: bytes the reader could not interpret as a record.
///
/// A capture is appended to across game runs, so the only damage it can suffer is a
/// record cut short by an abrupt termination — and once the next run has appended after
/// such a stub, the damage stays in the middle of the file forever. Skipping it costs
/// the records in the gap (usually a single chunk) instead of the whole world.
/// </summary>
public sealed record CaptureReadIssue(long Offset, long Length, string Reason, bool Recovered)
{
    public string Describe()
    {
        string position = $"byte {Offset}";
        return Recovered
            ? $"damaged capture data at {position}: {Reason}. {Length} bytes skipped, reading continues at the next record."
            : $"unreadable record at {position} ({Length} bytes left): {Reason}. The data after it is not read.";
    }
}

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
        CompressionLevel level = CompressionLevel.Fastest, Action<string>? log = null)
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
        else
        {
            TrimIncompleteTail(_stream, log);
        }
        _stream.Seek(0, SeekOrigin.End);
    }

    /// <summary>
    /// Drop a record left half-written by an abrupt termination. Appending after such a
    /// stub would embed the damage in the middle of the file, where it can never be
    /// removed again — the reader would have to skip it on every read forever.
    ///
    /// Only the tail is examined, and the search starts from the end: the last record
    /// whose body is complete and whose CRC matches is the file's real end. A clean file
    /// ends exactly there, so nothing is touched.
    /// </summary>
    private static void TrimIncompleteTail(FileStream stream, Action<string>? log)
    {
        try
        {
            long length = stream.Length;
            if (length < CaptureFormat.FileHeaderSize + CaptureFormat.RecordHeaderSize) return;

            long window = Math.Min(length - CaptureFormat.FileHeaderSize, CaptureFormat.TailScanBytes);
            long from = length - window;
            var tail = new byte[window];
            stream.Seek(from, SeekOrigin.Begin);
            if (!CaptureFormat.TryReadExactly(stream, tail)) return;

            for (long i = window - CaptureFormat.RecordHeaderSize; i >= 0; i--)
            {
                var span = tail.AsSpan((int)i);
                if (!CaptureFormat.IsRecordHeader(span)) continue;

                uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(span[8..12]);
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(span[16..20]);
                if (payloadLen > CaptureFormat.MaxRecordBytes) continue;
                if (i + CaptureFormat.RecordHeaderSize + payloadLen > window) continue;
                if (Crc32.Compute(span.Slice(CaptureFormat.RecordHeaderSize, (int)payloadLen)) != crc) continue;

                long end = from + i + CaptureFormat.RecordHeaderSize + payloadLen;
                if (end == length) return;

                stream.SetLength(end);
                log?.Invoke($"Capture tail trimmed: dropped {length - end} bytes of an incomplete record "
                            + $"(the game was terminated while it was being written).");
                return;
            }

            log?.Invoke("Capture tail: no complete record in the last "
                        + $"{CaptureFormat.TailScanBytes / (1024 * 1024)} MiB — appending as is.");
        }
        catch (Exception e)
        {
            log?.Invoke($"Capture tail not checked ({e.Message}) — appending as is.");
        }
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

/// <summary>
/// Streaming capture reader. A record that cannot be read is skipped, not fatal: the
/// damage an append-only file can collect (a record cut short by a killed game) would
/// otherwise make every later build fail forever.
/// </summary>
public static class CaptureReader
{
    /// <summary>CRC-valid records in a row a candidate offset must start with to be a record boundary.</summary>
    private const int ResyncRunLength = 3;

    /// <summary>How far ahead the reader looks for a record boundary after hitting damage.</summary>
    private const int ResyncWindow = 4 * 1024 * 1024;

    /// <summary>Upper bound on candidates tried per gap, so a large damaged region cannot stall a build.</summary>
    private const int ResyncCandidates = 64;

    private const int ScratchBytes = 256 * 1024;

    /// <summary>Read all capture records in order.</summary>
    public static IEnumerable<CaptureRecord> Read(string root, Action<CaptureReadIssue>? onIssue = null)
    {
        string path = Path.Combine(root, CaptureFormat.FileName);
        if (!File.Exists(path)) yield break;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < CaptureFormat.FileHeaderSize) yield break;

        var header = new byte[CaptureFormat.RecordHeaderSize];
        var scratch = new byte[ScratchBytes];
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
            if (!CaptureFormat.TryReadExactly(fs, header)) yield break;

            bool headerOk = CaptureFormat.IsRecordHeader(header);
            string reason = headerOk ? "a record body is not there" : "no record header";

            if (headerOk)
            {
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]);
                uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
                uint rawLen = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);

                if (payloadLen <= CaptureFormat.MaxRecordBytes
                    && offset + CaptureFormat.RecordHeaderSize + payloadLen <= fileLength)
                {
                    var body = new byte[(int)payloadLen];
                    if (ReadBody(fs, (int)payloadLen, scratch, body) && Crc32.Compute(body) == crc)
                    {
                        byte[] payload = (flags & CaptureFormat.FlagBrotli) != 0
                            ? CaptureFormat.Decompress(body, (int)rawLen)
                            : body;
                        yield return new CaptureRecord((CaptureRecordType)BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]),
                            payload, offset);
                        continue;
                    }

                    // The body is unreadable or does not match its checksum: the framing is
                    // broken from here on, so the next record has to be found by content.
                    reason = "a record body does not match its checksum";
                    if (TryReportGap(fs, offset + CaptureFormat.RecordHeaderSize, fileLength, offset, reason, scratch, onIssue))
                        continue;
                    onIssue?.Invoke(new CaptureReadIssue(offset, fileLength - offset, reason, false));
                    yield break;
                }
            }

            // Not a header, or a header whose body is not there. Retry one byte later: the
            // stub of a record whose header was cut in half still sits before good data.
            if (TryReportGap(fs, offset + 1, fileLength, offset, reason, scratch, onIssue)) continue;
            onIssue?.Invoke(new CaptureReadIssue(offset, fileLength - offset, reason, false));
            yield break;
        }
    }

    /// <summary>
    /// Look for the next record boundary after damage and, if one is found, move the
    /// stream to it. Returns false when nothing readable follows — then the capture has
    /// simply lost its tail, which is what an abrupt termination looks like.
    /// </summary>
    private static bool TryReportGap(FileStream fs, long from, long fileLength, long offset,
        string reason, byte[] scratch, Action<CaptureReadIssue>? onIssue)
    {
        long? next = FindBoundary(fs, from, fileLength, scratch);
        if (next == null) return false;

        onIssue?.Invoke(new CaptureReadIssue(offset, next.Value - offset, reason, true));
        fs.Seek(next.Value, SeekOrigin.Begin);
        return true;
    }

    /// <summary>
    /// The next offset that starts a run of intact records, or null if there is none
    /// within the search window.
    /// </summary>
    private static long? FindBoundary(FileStream fs, long from, long fileLength, byte[] scratch)
    {
        long saved = fs.Position;
        try
        {
            long end = Math.Min(fileLength, from + ResyncWindow);
            long length = end - from;
            if (length < CaptureFormat.RecordHeaderSize) return null;

            var window = new byte[length];
            fs.Seek(from, SeekOrigin.Begin);
            if (!CaptureFormat.TryReadExactly(fs, window)) return null;

            int candidates = 0;
            for (int i = 0; i + CaptureFormat.RecordHeaderSize <= window.Length; i++)
            {
                if (!CaptureFormat.IsRecordHeader(window.AsSpan(i, 4))) continue;
                if (++candidates > ResyncCandidates) return null;
                if (IsRecordRun(fs, from + i, fileLength, scratch)) return from + i;
            }
            return null;
        }
        finally
        {
            fs.Seek(saved, SeekOrigin.Begin);
        }
    }

    /// <summary>
    /// Whether the records at this offset parse cleanly. Three in a row make a boundary;
    /// fewer are accepted only if they lead exactly to the end of the file (the damaged
    /// record may be the one before the last).
    /// </summary>
    private static bool IsRecordRun(FileStream fs, long offset, long fileLength, byte[] scratch)
    {
        long saved = fs.Position;
        try
        {
            fs.Seek(offset, SeekOrigin.Begin);
            var header = new byte[CaptureFormat.RecordHeaderSize];
            int valid = 0;
            while (valid < ResyncRunLength)
            {
                long here = fs.Position;
                if (here + CaptureFormat.RecordHeaderSize > fileLength) break;
                if (!CaptureFormat.TryReadExactly(fs, header)) break;
                if (!CaptureFormat.IsRecordHeader(header)) break;

                uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
                if (payloadLen > CaptureFormat.MaxRecordBytes) break;
                if (here + CaptureFormat.RecordHeaderSize + payloadLen > fileLength) break;
                if (!VerifyBody(fs, (int)payloadLen, crc, scratch)) break;
                valid++;
            }

            return valid >= ResyncRunLength || (valid > 0 && fs.Position == fileLength);
        }
        finally
        {
            fs.Seek(saved, SeekOrigin.Begin);
        }
    }

    /// <summary>Read a body into <paramref name="into"/>; false if the stream ended first.</summary>
    private static bool ReadBody(FileStream fs, int payloadLen, byte[] scratch, byte[] into)
    {
        int remaining = payloadLen;
        int written = 0;
        while (remaining > 0)
        {
            int n = fs.Read(scratch, 0, Math.Min(scratch.Length, remaining));
            if (n <= 0) return false;
            scratch.AsSpan(0, n).CopyTo(into.AsSpan(written));
            written += n;
            remaining -= n;
        }
        return true;
    }

    /// <summary>Check a body's CRC without keeping it.</summary>
    private static bool VerifyBody(FileStream fs, int payloadLen, uint crc, byte[] scratch)
    {
        int remaining = payloadLen;
        uint state = 0;
        while (remaining > 0)
        {
            int n = fs.Read(scratch, 0, Math.Min(scratch.Length, remaining));
            if (n <= 0) return false;
            state = Crc32.Compute(state, scratch.AsSpan(0, n));
            remaining -= n;
        }
        return state == crc;
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
    public static (Dictionary<CaptureRecordType, long> Counts, long TotalBytes) Summarize(string root,
        Action<CaptureReadIssue>? onIssue = null)
    {
        var counts = new Dictionary<CaptureRecordType, long>();
        long total = 0;
        foreach (var record in Read(root, onIssue))
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
}
