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

    /// <summary>
    /// Entities by id. "Last record wins" applies: an entity is written at spawn,
    /// then re-written with a fresh position and state while it is in the world, and
    /// removed by an <see cref="CaptureRecordType.EntityDespawn"/> record.
    ///
    /// Entity ids are per-world counters, so (exactly like the block registry) a
    /// capture that spans several connections mixes them.
    /// </summary>
    public readonly Dictionary<long, EntityPayload> Entities = new();

    /// <summary>
    /// The clock and the player position at the end of the capture ("last record wins"):
    /// the state is re-written while the world runs, and the builder needs the last one.
    /// null means the capture has none — an old capture, or capture of the world state
    /// turned off. The world then opens at its own start time, as it did before.
    /// </summary>
    public WorldStatePayload? WorldState;

    public long RecordsRead;

    /// <summary>
    /// Gaps in the capture file the reader had to step over. A capture is appended to
    /// across game runs, so damage from a killed game stays in the middle of the file:
    /// these records are gone, everything after them is still read.
    /// </summary>
    public int DamagedRegions;

    /// <summary>Bytes lost to <see cref="DamagedRegions"/>.</summary>
    public long DamagedBytes;

    /// <summary>Whether the capture ends with a record that was cut short (the usual result of an abrupt termination).</summary>
    public bool TruncatedTail;

    /// <summary>
    /// How many server connections are reflected in the capture (by the number of
    /// identification records). The file is append-only and survives between game
    /// runs, so one directory may hold data from several servers.
    /// </summary>
    public int Connections;

    /// <summary>Entities removed by an <see cref="CaptureRecordType.EntityDespawn"/> record.</summary>
    public long Despawns;

    /// <summary>
    /// Despawns by the reason the game gave (a byte of <c>EnumDespawnReason</c>;
    /// <see cref="EntityDespawnPayload.Unknown"/> groups the records written before
    /// the reason was stored). Diagnostics only.
    /// </summary>
    public readonly Dictionary<byte, int> DespawnReasons = new();

    /// <summary>Connection whose records are being read right now (0 before the first identification record).</summary>
    private int _connection;

    /// <summary>Chunk sections received by the connection being read, with the offset of the record — see <see cref="FinalizeConnection"/>.</summary>
    private readonly List<((int X, int Y, int Z) Key, long Offset)> _connectionChunks = [];

    /// <summary>Whether the connection being read recorded an entity at all.</summary>
    private bool _connectionHadEntities;

    /// <summary>Offset of the last entity record of the connection being read (-1 if it has none).</summary>
    private long _connectionLastEntityOffset = -1;

    /// <summary>Connection in which each chunk section was last received.</summary>
    public readonly Dictionary<(int X, int Y, int Z), int> ChunkConnections = new();

    /// <summary>
    /// Per chunk section, the last connection that is still current for its entities: one
    /// that received the chunk and then went on recording entities. An entity is stale when
    /// this is later than the entity's own last record — the client looked at that place
    /// again and the entity was not in it.
    ///
    /// Both halves are needed, and in this order. Receiving a chunk is not evidence on its
    /// own: a session can load chunks and record no entity at all, and "the client said
    /// nothing" is not "there is nothing". The entity half comes from the mod's refresh pass
    /// over the loaded entities: while a connection records entities at all, the client's
    /// whole tracked set is written over and over, so a live entity standing in a loaded
    /// chunk cannot stay silent. The chunk has to have arrived BEFORE those records — a
    /// chunk that came last has not been looked at with entity tracking yet.
    ///
    /// A chunk that is not received again keeps its entities: no information about a place
    /// is no reason to erase it.
    /// </summary>
    public readonly Dictionary<(int X, int Y, int Z), int> SupersedeConnections = new();

    /// <summary>Connections that recorded at least one entity — only those can make an entity stale.</summary>
    public int ConnectionsWithEntities;

    /// <summary>
    /// Chunk sections whose last receipt was not followed by entity records, so there is no
    /// newer entity information about them and their entities are kept as they were. This is
    /// the number that explains leftovers — see <see cref="SupersedeConnections"/>.
    /// </summary>
    public int ChunksWithoutNewerEntityInfo
    {
        get
        {
            int count = 0;
            foreach (var (key, connection) in ChunkConnections)
            {
                if (SupersedeConnections.GetValueOrDefault(key, -1) != connection) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Entities by how many connections ago they were last mentioned (0 = the last one).
    /// Entities that were never stamped (a capture with no identification records at all)
    /// are not listed. This says how much of the capture is fresh knowledge: an entity
    /// seen several connections ago may have been dead on the server for a long time,
    /// because a client only learns about a despawn for an entity it is tracking.
    /// </summary>
    public SortedDictionary<int, int> EntitiesByAge()
    {
        var byAge = new SortedDictionary<int, int>();
        foreach (var entity in Entities.Values)
        {
            if (entity.LastConnection <= 0) continue;
            int age = System.Math.Max(0, Connections - entity.LastConnection);
            byAge[age] = byAge.GetValueOrDefault(age) + 1;
        }
        return byAge;
    }

    /// <summary>
    /// Close the connection being read. What it supersedes is only known once its records
    /// end — the next identification record, or the end of the file — because the answer is
    /// about the connection as a whole: did any entity record follow each chunk.
    /// </summary>
    private void FinalizeConnection()
    {
        if (_connectionHadEntities && _connection > 0)
        {
            ConnectionsWithEntities++;
            // Connections only grow, so the plain assignment is already "the last one".
            foreach (var (key, offset) in _connectionChunks)
            {
                // A chunk that arrived after the last entity record of the session was never
                // looked at with entity tracking: the refresh pass has not run over it yet.
                if (offset < _connectionLastEntityOffset) SupersedeConnections[key] = _connection;
            }
        }
        _connectionChunks.Clear();
        _connectionHadEntities = false;
        _connectionLastEntityOffset = -1;
    }

    public int MapSizeX => Identification?.MapSizeX ?? 0;
    public int MapSizeY => Identification?.MapSizeY ?? 256;
    public int MapSizeZ => Identification?.MapSizeZ ?? 0;
    /// <summary>Number of sections along Y if it is known from the server identification. Otherwise -1.</summary>
    public int KnownSectionsPerColumn => Identification != null && Identification.MapSizeY > 0
        ? Identification.MapSizeY / 32
        : -1;

    public bool SectionsInferred => KnownSectionsPerColumn < 0;

    /// <summary>
    /// Highest entity id in the capture (0 if there are none). The game continues the
    /// numbering from <c>SaveGame.LastEntityId</c>, so the assembled world has to start
    /// above the captured ids — otherwise the first mobs the world spawns itself collide
    /// with the captured ones.
    /// </summary>
    public long HighestEntityId
    {
        get
        {
            long max = 0;
            foreach (long id in Entities.Keys)
            {
                if (id > max) max = id;
            }
            return max;
        }
    }

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
        foreach (var record in CaptureReader.Read(captureDir, issue => model.Report(issue, log)))
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

        // The last connection has no successor record to close it.
        model.FinalizeConnection();
        return model;
    }

    private void Report(CaptureReadIssue issue, Action<string>? log)
    {
        if (issue.Recovered)
        {
            DamagedRegions++;
            DamagedBytes += issue.Length;
        }
        else
        {
            TruncatedTail = true;
        }
        log?.Invoke("  " + issue.Describe());
    }

    private void Accept(CaptureRecord record)
    {
        switch (record.Type)
        {
            case CaptureRecordType.ServerIdentification:
                FinalizeConnection();
                Connections++;
                _connection = Connections;
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
                var key = (chunk.X, chunk.Y, chunk.Z);
                Chunks[key] = chunk;
                ChunkConnections[key] = _connection;
                _connectionChunks.Add((key, record.Offset));
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

            case CaptureRecordType.Entity:
            {
                var entity = CapturePayloadCodec.DecodeEntity(record.Payload);
                entity.LastConnection = _connection;
                Entities[entity.EntityId] = entity;
                _connectionHadEntities = true;
                _connectionLastEntityOffset = record.Offset;
                break;
            }

            case CaptureRecordType.EntityDespawn:
            {
                var despawn = CapturePayloadCodec.DecodeEntityDespawn(record.Payload);
                Entities.Remove(despawn.EntityId);
                Despawns++;
                DespawnReasons[despawn.Reason] = DespawnReasons.GetValueOrDefault(despawn.Reason) + 1;
                break;
            }

            case CaptureRecordType.WorldState:
                WorldState = CapturePayloadCodec.DecodeWorldState(record.Payload);
                break;
        }
    }

    /// <summary>
    /// Entities grouped by the chunk section their position falls into, so that the
    /// builder can take them per chunk. The grouping is done once per call: there can
    /// be tens of thousands of chunks, and looking up every entity per chunk would be
    /// quadratic.
    /// </summary>
    public Dictionary<(int X, int Y, int Z), List<EntityPayload>> GroupEntitiesByChunk()
        => GroupEntitiesByChunk(-1, out _);

    /// <summary>
    /// The same grouping, without the entities that lost their relevance:
    ///
    ///   • superseded — the chunk they stand in was received again in a later connection
    ///     that recorded entities, and they were not among them (see
    ///     <see cref="SupersedeConnections"/>). This is the rule that keeps a capture
    ///     appended to across sessions from carrying mobs that died while the player was
    ///     away; it is scoped to the entity's own chunk, so a place nobody returned to
    ///     keeps its inhabitants.
    ///   • too old — only when <paramref name="maxAgeConnections"/> is positive: no record
    ///     mentioned them for more than that many connections. A bound on top of the rule
    ///     above, for a capture whose newest connections never covered their chunk.
    ///
    /// What was left out and why is returned in <paramref name="scope"/>.
    /// </summary>
    public Dictionary<(int X, int Y, int Z), List<EntityPayload>> GroupEntitiesByChunk(
        int maxAgeConnections, out EntityScope scope)
    {
        var byChunk = new Dictionary<(int X, int Y, int Z), List<EntityPayload>>();
        long kept = 0, superseded = 0, tooOld = 0;
        foreach (var entity in Entities.Values)
        {
            // A non-finite coordinate is not a position: such an entity would land in
            // an arbitrary chunk (or break the section number), so it is dropped.
            if (!double.IsFinite(entity.X) || !double.IsFinite(entity.Y) || !double.IsFinite(entity.Z)) continue;

            var key = (FloorDiv32(entity.X), FloorDiv32(entity.Y), FloorDiv32(entity.Z));

            // An entity of a capture without identification records has no age at all —
            // it is kept, rather than silently thrown away on an unknown.
            if (entity.LastConnection > 0
                && SupersedeConnections.TryGetValue(key, out int supersedeConnection)
                && supersedeConnection > entity.LastConnection)
            {
                superseded++;
                continue;
            }

            if (maxAgeConnections > 0 && entity.LastConnection > 0
                && Connections - entity.LastConnection > maxAgeConnections)
            {
                tooOld++;
                continue;
            }

            if (!byChunk.TryGetValue(key, out var list))
            {
                list = [];
                byChunk[key] = list;
            }
            list.Add(entity);
            kept++;
        }

        scope = new EntityScope(kept, superseded, tooOld);
        return byChunk;
    }

    /// <summary>
    /// Chunk or section number of a world coordinate: floor division by 32. Floor
    /// rather than truncation, otherwise everything from -1 to -31 would land in
    /// chunk 0 (shifting an int does floor division for negatives as well).
    /// </summary>
    private static int FloorDiv32(double value) => (int)Math.Floor(value) >> 5;

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

/// <summary>
/// What <see cref="CaptureModel.GroupEntitiesByChunk(int, out EntityScope)"/> did with the
/// entities of the capture: how many it kept and how many it left out, by reason.
/// </summary>
public readonly record struct EntityScope(long Kept, long Superseded, long TooOld);
