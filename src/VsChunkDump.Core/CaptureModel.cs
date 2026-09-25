namespace VsChunkDump.Core;

/// <summary>
/// Parsed capture: everything the server sent to the client, in flat structures.
/// For objects sent again, the 'last record wins' rule applies.
/// </summary>
public sealed class CaptureModel
{
    public WorldInfoPayload? Identification;
    public LevelInitPayload? LevelInitialize;
    public WorldMetaPayload? WorldMetaData;
    public BlockRegistryPayload Assets = new();

    /// <summary>Chunks by coordinates (X, Y-section, Z).</summary>
    public readonly Dictionary<(int X, int Y, int Z), ChunkPayload> Chunks = new();

    /// <summary>Column heightmaps by (X, Z).</summary>
    public readonly Dictionary<(int X, int Z), MapChunkPayload> MapChunks = new();

    /// <summary>
    /// Block entity updates outside a chunk (packet id 48), by world position.
    /// Applied to chunks last: the server sends them when a block entity
    /// changes after the chunk has already been received.
    /// </summary>
    public readonly Dictionary<(int X, int Y, int Z), BlockEntityPayload> BlockEntityUpdates = new();

    public long RecordsRead;

    /// <summary>
    /// How many server connections are reflected in the capture (by the number of
    /// identification records). The file is append-only and survives between game
    /// runs, so one directory may hold data from several servers.
    /// </summary>
    public int Connections;

    public int MapSizeX => Identification?.MapSizeX ?? 0;
    public int MapSizeY => Identification?.MapSizeY ?? 256;
    public int MapSizeZ => Identification?.MapSizeZ ?? 0;
    /// <summary>Number of sections along Y if it is known from the server identification. Otherwise -1.</summary>
    public int KnownSectionsPerColumn => Identification != null && Identification.MapSizeY > 0
        ? Identification.MapSizeY / 32
        : -1;

    public bool SectionsInferred => KnownSectionsPerColumn < 0;

    /// <summary>
    /// How many sections a column has. The identification packet arrives on connect
    /// and may have been missed, so when it is absent we infer the height from the
    /// capture itself: the server sends the whole column, so the highest section
    /// number among all chunks is the top of the world.
    /// </summary>
    public int SectionsPerColumn
    {
        get
        {
            int known = KnownSectionsPerColumn;
            if (known > 0) return known;

            int maxSection = -1;
            foreach (var key in Chunks.Keys)
            {
                if (key.Y > maxSection) maxSection = key.Y;
            }
            return maxSection < 0 ? 1 : maxSection + 1;
        }
    }

    public static CaptureModel Load(string captureDir, Action<string>? log = null)
    {
        var model = new CaptureModel();
        foreach (var record in CaptureReader.Read(captureDir))
        {
            model.RecordsRead++;
            try
            {
                model.Accept(record);
            }
            catch (Exception e)
            {
                log?.Invoke($"  skipped record {record.Type} @ {record.Offset}: {e.Message}");
            }
        }
        return model;
    }

    private void Accept(CaptureRecord record)
    {
        switch (record.Type)
        {
            case CaptureRecordType.ServerIdentification:
                Connections++;
                Identification = CapturePayloadCodec.DecodeWorldInfo(record.Payload);
                break;

            case CaptureRecordType.LevelInitialize:
                LevelInitialize = CapturePayloadCodec.DecodeLevelInit(record.Payload);
                break;

            case CaptureRecordType.WorldMetaData:
                WorldMetaData = CapturePayloadCodec.DecodeWorldMeta(record.Payload);
                break;

            case CaptureRecordType.ServerAssets:
                Assets = CapturePayloadCodec.DecodeBlockRegistry(record.Payload);
                break;

            case CaptureRecordType.Chunk:
            {
                var chunk = CapturePayloadCodec.DecodeChunk(record.Payload);
                Chunks[(chunk.X, chunk.Y, chunk.Z)] = chunk;
                break;
            }

            case CaptureRecordType.MapChunk:
            {
                var mapChunk = CapturePayloadCodec.DecodeMapChunk(record.Payload);
                MapChunks[(mapChunk.ChunkX, mapChunk.ChunkZ)] = mapChunk;
                break;
            }

            case CaptureRecordType.MapRegion:
                // Region contents are not retained (see PacketCapturePatch).
                break;

            case CaptureRecordType.BlockEntityUpdate:
            {
                var update = CapturePayloadCodec.DecodeBlockEntityUpdate(record.Payload);
                foreach (var be in update.BlockEntities) BlockEntityUpdates[(be.X, be.Y, be.Z)] = be;
                break;
            }
        }
    }

    /// <summary>
    /// Block entities for a chunk: those that came inside the chunk itself plus
    /// updates that arrived later (by position they override the in-chunk ones).
    /// </summary>
    public List<BlockEntityPayload> BlockEntitiesFor(ChunkPayload chunk)
    {
        var result = new List<BlockEntityPayload>(chunk.BlockEntities);
        if (BlockEntityUpdates.Count == 0) return result;

        var replaced = new HashSet<(int X, int Y, int Z)>();
        for (int i = 0; i < result.Count; i++)
        {
            if (!BlockEntityUpdates.TryGetValue((result[i].X, result[i].Y, result[i].Z), out var update)) continue;
            result[i] = update;
            replaced.Add((update.X, update.Y, update.Z));
        }

        foreach (var update in BlockEntityUpdates.Values)
        {
            if (replaced.Contains((update.X, update.Y, update.Z))) continue;
            // The update belongs to this chunk if its position is inside it.
            if (update.X >> 5 != chunk.X || update.Z >> 5 != chunk.Z) continue;
            if (update.Y < 0 || update.Y >> 5 != chunk.Y) continue;
            result.Add(update);
        }

        return result;
    }

    /// <summary>
    /// Column readiness for writing per the ServerSystemSupplyChunks gate rules:
    /// a column must be captured in full and have a mapchunk row, otherwise the game
    /// silently regenerates it and our chunks are lost.
    /// </summary>
    public (int Complete, int NoMapChunk, int Incomplete) ColumnReadiness()
    {
        int sections = SectionsPerColumn;
        int complete = 0, noMapChunk = 0, incomplete = 0;

        foreach (var column in Chunks.Values.GroupBy(c => (c.X, c.Z)))
        {
            var present = column.Select(c => c.Y).Distinct().ToList();
            bool isComplete = present.Count == sections
                              && present.Min() == 0
                              && present.Max() == sections - 1;
            if (!isComplete) { incomplete++; continue; }
            if (!MapChunks.ContainsKey(column.Key)) { noMapChunk++; continue; }
            complete++;
        }

        return (complete, noMapChunk, incomplete);
    }

    /// <summary>Block registry 'id -> code' from the assets packet.</summary>
    public Dictionary<int, string> BlockRegistry()
    {
        var map = new Dictionary<int, string>();
        foreach (var entry in Assets.Blocks) map[entry.BlockId] = entry.Code;
        return map;
    }

    /// <summary>Server mods (id@version) — they show whether the set matches the local one.</summary>
    public List<string> ServerMods()
    {
        var mods = new List<string>();
        if (Identification == null) return mods;
        foreach (var mod in Identification.Mods) mods.Add($"{mod.Id}@{mod.Version}");
        return mods;
    }
}
