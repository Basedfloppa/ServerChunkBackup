using System.Text.Json;
using System.Text.Json.Serialization;

namespace VsChunkDump.Core;

/// <summary>
/// Block registry 'id → code' in a portable form: a JSON file that can be
/// taken from a server (from a capture) or from a local world (from its BlockIDs).
///
/// Why this is needed: block ids depend on the mod set and on who built the
/// registry. The server numbers blocks its own way, the local world its own way, and
/// the divergence starts with the very first ids. To put server blobs into a
/// local world, the ids must be translated, and that needs BOTH registries:
/// the server one (from the capture) and the local one (this file).
/// </summary>
public sealed class BlockRegistryTable
{
    private const string FormatName = "vsblockregistry";
    private const int FormatVersion = 1;

    public string? GameVersion { get; set; }

    /// <summary>Where the registry was taken from — for humans; does not affect parsing.</summary>
    public string? Source { get; set; }

    /// <summary>'block id → code' (for example 2 → "game:meta-filler").</summary>
    public Dictionary<int, string> Blocks { get; } = new();

    public int Count => Blocks.Count;

    public int MaxId
    {
        get
        {
            int max = -1;
            foreach (int id in Blocks.Keys) if (id > max) max = id;
            return max;
        }
    }

    /// <summary>
    /// Whether the numbering has gaps. A 'dense' registry (maxId + 1 == Count) is a sign
    /// that the registry was taken from a live world in full, rather than assembled by a
    /// remapper on top of someone else's numbering.
    /// </summary>
    public bool IsDense => Count > 0 && MaxId + 1 == Count;

    /// <summary>
    /// Normalize a block code to a single form: 'game:air' instead of 'air'.
    ///
    /// This is necessary because the sources write the code differently:
    ///   • the server assets packet and the BlockIDs table in a save — without a domain
    ///     for game blocks ('air', 'meta-filler') and with a domain for mods;
    ///   • the archiver dump and some tools — always with a domain ('game:air').
    /// They cannot be compared as-is: half of the game blocks would appear
    /// to be missing.
    /// </summary>
    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        return code.Contains(':') ? code : "game:" + code;
    }

    public static BlockRegistryTable FromDictionary(IReadOnlyDictionary<int, string> blocks)
    {
        var table = new BlockRegistryTable();
        foreach (var pair in blocks) table.Blocks[pair.Key] = pair.Value;
        return table;
    }

    /// <summary>
    /// Read a registry from JSON. Understands two record shapes:
    ///   {"format":"vsblockregistry","version":1,"blocks":{"0":"game:air",...}}
    ///   {"blocks":[[0,"game:air"],[1,"game:mantle"],...]}
    /// The second shape is convenient to generate with scripts.
    /// </summary>
    public static BlockRegistryTable Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, path);
    }

    private static BlockRegistryTable Load(Stream stream, string? origin = null)
    {
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var root = doc.RootElement;
        var table = new BlockRegistryTable();

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("format", out var format)
                && format.ValueKind == JsonValueKind.String
                && !string.Equals(format.GetString(), FormatName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"'{origin ?? "stream"}' is not a block registry: format = '{format.GetString()}', expected '{FormatName}'.");
            }

            if (root.TryGetProperty("version", out var version) && version.TryGetInt32(out int v) && v > FormatVersion)
            {
                throw new InvalidDataException(
                    $"Registry '{origin ?? "stream"}' is version {v}, but this program understands up to {FormatVersion}.");
            }

            if (root.TryGetProperty("gameVersion", out var gv) && gv.ValueKind == JsonValueKind.String)
                table.GameVersion = gv.GetString();
            if (root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
                table.Source = src.GetString();
        }

        if (!TryGetBlocks(root, out var blocks))
            throw new InvalidDataException($"'{origin ?? "stream"}' has no 'blocks' field with a block registry.");

        if (blocks.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in blocks.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out int id))
                    throw new InvalidDataException($"'{entry.Name}' is not a numeric block id.");
                table.Blocks[id] = entry.Value.GetString() ?? "";
            }
        }
        else
        {
            foreach (var entry in blocks.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
                    throw new InvalidDataException("A 'blocks' element must be a pair [id, code].");
                int id = entry[0].ValueKind == JsonValueKind.Number
                    ? entry[0].GetInt32()
                    : int.Parse(entry[0].GetString() ?? "");
                table.Blocks[id] = entry[1].GetString() ?? "";
            }
        }

        if (table.Blocks.Count == 0)
            throw new InvalidDataException($"Registry '{origin ?? "stream"}' is empty.");

        return table;
    }

    private static bool TryGetBlocks(JsonElement root, out JsonElement blocks)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("blocks", out blocks)) return true;
        // Also accept a bare id → code dictionary without a wrapper.
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (int.TryParse(prop.Name, out _) && prop.Value.ValueKind == JsonValueKind.String)
                {
                    blocks = root;
                    return true;
                }
            }
        }
        blocks = default;
        return false;
    }

    public void Save(string path, string? source = null)
    {
        var payload = new OrderedRegistry
        {
            Format = FormatName,
            Version = FormatVersion,
            GameVersion = GameVersion,
            Source = source ?? Source,
            Count = Blocks.Count,
            Blocks = Blocks.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, payload, options);
    }

    private sealed class OrderedRegistry
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? GameVersion { get; set; }
        public string? Source { get; set; }
        public int Count { get; set; }
        public Dictionary<string, string>? Blocks { get; set; }
    }
}

/// <summary>Result of the id translation: the map plus counters for the report.</summary>
public sealed class BlockIdMapResult
{
    /// <summary>'server id → id in this world'. For ids without a local code — <see cref="FallbackId"/>.</summary>
    public Dictionary<int, int> Map { get; } = new();

    public int SourceCount;
    public int TargetCount;

    /// <summary>How many ids stayed in place.</summary>
    public int Same;

    /// <summary>How many ids moved (server id ≠ local id).</summary>
    public int Moved;

    /// <summary>How many server blocks were not found locally (they go to <see cref="FallbackId"/>).</summary>
    public int Missing;

    /// <summary>How many server ids point to the same local id as another server id.</summary>
    public int Collapsed;

    /// <summary>Examples of missing codes — to show which mods are absent.</summary>
    public List<string> MissingSamples { get; } = new();

    public int FallbackId { get; set; }

    /// <summary>Nothing to translate: the numbering matches.</summary>
    public bool IsIdentity => Moved == 0 && Missing == 0;

    public string Describe()
    {
        return $"id translation: {Map.Count} entries (same {Same}, moved {Moved}, "
               + $"missing locally {Missing}, collapsed targets {Collapsed})";
    }
}

/// <summary>
/// Builds the 'server id → local id' translation by block code. The code is
/// the only thing that identifies a block: every registry has its own ids.
/// </summary>
public static class BlockIdMapBuilder
{
    /// <param name="source">Registry whose ids the data is written in (the server from the capture).</param>
    /// <param name="target">Registry of the world the data is put into (local).</param>
    /// <param name="fallbackId">What to put for blocks that are not present locally (air by default).</param>
    public static BlockIdMapResult Build(
        IReadOnlyDictionary<int, string> source,
        IReadOnlyDictionary<int, string> target,
        int fallbackId = 0)
    {
        var result = new BlockIdMapResult { FallbackId = fallbackId, SourceCount = source.Count, TargetCount = target.Count };

        // Code → local id. If a code repeats in the local registry (a broken
        // mod set), take the smallest id — that keeps the result deterministic.
        var targetByCode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in target.OrderBy(kv => kv.Key))
        {
            string code = BlockRegistryTable.NormalizeCode(pair.Value);
            if (code.Length == 0) continue;
            if (!targetByCode.ContainsKey(code)) targetByCode[code] = pair.Key;
        }

        var usedTargets = new HashSet<int>();
        foreach (var pair in source.OrderBy(kv => kv.Key))
        {
            int id = pair.Key;
            string code = BlockRegistryTable.NormalizeCode(pair.Value);
            if (targetByCode.TryGetValue(code, out int localId))
            {
                result.Map[id] = localId;
                if (localId == id) result.Same++;
                else result.Moved++;
                if (!usedTargets.Add(localId)) result.Collapsed++;
            }
            else
            {
                result.Map[id] = fallbackId;
                result.Missing++;
                if (result.MissingSamples.Count < 12) result.MissingSamples.Add(code);
            }
        }

        return result;
    }
}
