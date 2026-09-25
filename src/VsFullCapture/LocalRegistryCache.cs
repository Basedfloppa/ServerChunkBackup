using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Local block registry: "id in THIS world → code". It is used to translate the
/// numbering of server blobs when building the world.
///
/// Why a snapshot is needed. In multiplayer the client does not build the
/// registry itself: it receives the block list from the server and lays them out
/// by the ids sent by the server (ClientSystemStartup.LoadBlockTypes). That is,
/// during a session on a server the client holds the SERVER registry, and there
/// is nothing to translate with it. Its own local registry is only visible where
/// a local server runs — that is, in singleplayer.
///
/// The snapshot is only taken for "dense" numbering without gaps and without
/// placeholder blocks. For a world that has been opened with foreign ids, the
/// numbering was mixed by the remapper, and it is not suitable as a local one.
/// </summary>
public static class LocalRegistryCache
{
    public const string FileName = "localregistry.json";

    public static string SnapshotPath(ICoreClientAPI api)
        => Path.Combine(api.GetOrCreateDataPath("FullCapture"), FileName);

    /// <summary>
    /// Take the local registry to a file if now is a suitable moment. Returns a
    /// description of the result for the log.
    /// </summary>
    public static string TrySnapshot(ICoreClientAPI api, bool verbose)
    {
        if (!api.IsSinglePlayer) return "not singleplayer";

        var blocks = api.World?.Blocks;
        if (blocks == null || blocks.Count == 0) return "block registry not loaded yet";

        var table = new BlockRegistryTable
        {
            GameVersion = Vintagestory.API.Config.GameVersion.ShortGameVersion,
            Source = "snapshot from singleplayer"
        };

        int missing = 0;
        for (int id = 0; id < blocks.Count; id++)
        {
            Block? block = blocks[id];
            if (block == null) continue;
            if (block.IsMissing) { missing++; continue; }
            var code = block.Code;
            if (code == null) continue;
            table.Blocks[id] = code.ToShortString();
        }

        if (missing > 0)
        {
            return $"the registry has {missing} placeholder blocks — the world was opened with foreign numbering, "
                   + "not taking a snapshot (a world created here is required)";
        }

        if (table.Count < 64)
            return $"too few blocks ({table.Count}) — the registry is not ready yet";

        if (!table.IsDense)
        {
            return $"numbering has gaps ({table.Count} blocks, max id {table.MaxId}) — not taking a snapshot";
        }

        string path = SnapshotPath(api);
        table.Save(path, "snapshot from singleplayer");
        return $"local registry snapshot: {table.Count} blocks → {path}";
    }

    /// <summary>
    /// Load the registry: first the one explicitly set in the settings, then the
    /// snapshot. null — if there is neither.
    /// </summary>
    public static BlockRegistryTable? Load(FullCaptureConfig config, ICoreClientAPI api, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(config.LocalRegistryPath))
        {
            string path = config.LocalRegistryPath!;
            if (!File.Exists(path))
            {
                log($"Local registry file \"{path}\" not found.");
                return null;
            }
            var explicitTable = BlockRegistryTable.Load(path);
            log($"Local registry from settings: {explicitTable.Count} blocks (\"{path}\")");
            return explicitTable;
        }

        string snapshot = SnapshotPath(api);
        if (!File.Exists(snapshot))
        {
            log($"No local registry snapshot (\"{snapshot}\").");
            return null;
        }

        var table = BlockRegistryTable.Load(snapshot);
        log($"Local registry from snapshot: {table.Count} blocks (max id {table.MaxId})");
        return table;
    }
}
