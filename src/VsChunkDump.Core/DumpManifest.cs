using System.Text.Json;
using System.Text.Json.Serialization;

namespace VsChunkDump.Core;

/// <summary>
/// Table 'block id -&gt; block code'. Built on the client from <c>capi.World.Blocks</c>
/// and saved into the manifest so that the dump can be read without launching the game.
/// </summary>
public sealed class BlockTable
{
    private readonly string[] _codes;

    public BlockTable(IEnumerable<string?> codes)
    {
        _codes = codes.Select(c => c ?? "air").ToArray();
    }

    public int Count => _codes.Length;

    /// <summary>Block code such as <c>game:rock-granite</c>. For unknown ids — <c>unknown:&lt;id&gt;</c>.</summary>
    public string Code(int blockId)
    {
        if ((uint)blockId < (uint)_codes.Length) return _codes[blockId];
        return "unknown:" + blockId;
    }

    /// <summary>Human-readable name: the domain is dropped, the path is kept.</summary>
    public string ShortCode(int blockId)
    {
        string c = Code(blockId);
        int colon = c.IndexOf(':');
        return colon >= 0 ? c[(colon + 1)..] : c;
    }

    public IReadOnlyList<string> Codes => _codes;

    public static BlockTable Empty { get; } = new(Array.Empty<string>());
}

/// <summary>Dump metadata (&lt;root&gt;/manifest.json).</summary>
public sealed class DumpManifest
{
    [JsonPropertyName("format")] public string Format { get; set; } = DumpFormat.FormatName;

    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = DumpFormat.FormatVersion;

    [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }

    [JsonPropertyName("modVersion")] public string? ModVersion { get; set; }

    [JsonPropertyName("worldName")] public string? WorldName { get; set; }

    [JsonPropertyName("serverAddress")] public string? ServerAddress { get; set; }

    [JsonPropertyName("savegameIdentifier")] public string? SavegameIdentifier { get; set; }

    [JsonPropertyName("seed")] public int Seed { get; set; }

    [JsonPropertyName("chunkSize")] public int ChunkSize { get; set; } = ChunkGeometry.ChunkSize;

    [JsonPropertyName("mapSizeY")] public int MapSizeY { get; set; } = ChunkGeometry.DefaultMapSizeY;

    [JsonPropertyName("sectionCountY")] public int SectionCountY { get; set; } = ChunkGeometry.DefaultSectionCountY;

    [JsonPropertyName("chunkRegionSizeInChunks")]
    public int ChunkRegionSizeInChunks { get; set; } = ChunkGeometry.RegionSizeInChunks;

    [JsonPropertyName("modList")] public List<string> ModList { get; set; } = [];

    /// <summary>Block codes; the index in the list == the block id.</summary>
    [JsonPropertyName("blockCodes")] public List<string> BlockCodes { get; set; } = [];

    [JsonPropertyName("createdUtc")] public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedUtc")] public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>How many chunk records have been written over the whole lifetime (including duplicates).</summary>
    [JsonPropertyName("recordsWritten")] public long RecordsWritten { get; set; }

    /// <summary>How many unique chunks the dump holds (updated on close).</summary>
    [JsonPropertyName("uniqueChunks")] public long UniqueChunks { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Save(string root)
    {
        UpdatedUtc = DateTimeOffset.UtcNow;
        string path = Path.Combine(root, DumpFormat.ManifestFileName);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static DumpManifest Load(string root)
    {
        string path = Path.Combine(root, DumpFormat.ManifestFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Dump manifest not found: {path}", path);
        var m = JsonSerializer.Deserialize<DumpManifest>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("Failed to read manifest.json");
        if (!string.Equals(m.Format, DumpFormat.FormatName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unknown dump format: {m.Format}");
        return m;
    }
}
