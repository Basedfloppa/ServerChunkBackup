using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Capture of entities (mobs, dropped items, item frames).
///
/// Unlike chunks and block entities, an entity has no packet to take it from: over the
/// network it travels in "sync" form (<c>Entity.FromBytes(reader, isSync: true)</c>),
/// and the savegame form cannot be assembled from that (see <see cref="EntitySaveData"/>).
/// So the mod takes the bytes from the live entity object.
///
/// The events give the entity already initialized, and they fire on the MAIN thread:
/// SystemNetworkProcess hands every packet except the background ones (chunks) to the
/// main thread, and the entity systems trigger the events from there. That is what the
/// serialization needs — it reads live state and writes into WatchedAttributes.
///
/// But events alone are not enough: positions come over UDP
/// (<c>Packet_UdpPacket.BulkPositions</c>), which the capture never sees, so a constant
/// refresh pass over <see cref="Vintagestory.API.Client.IClientWorldAccessor.LoadedEntities"/>
/// keeps the positions current.
/// </summary>
internal static class EntityCapture
{
    /// <summary>Class name by CLR type. The whole class lives on the main thread, so a plain dictionary is enough.</summary>
    private static readonly Dictionary<Type, string?> Classnames = [];

    /// <summary>Ids written in the current session — "last record wins" needs nothing else.</summary>
    private static readonly HashSet<long> Written = [];

    private static ICoreClientAPI? _capi;
    private static ILogger? _logger;

    /// <summary>Current refresh pass over the loaded entities (spread over ticks).</summary>
    private static List<Entity>? _sweep;
    private static int _sweepIndex;
    private static long _lastSweepMs;

    private static long _captured, _skipped, _failed;

    /// <summary>Despawn records written in this session, by the reason the game gave.</summary>
    private static readonly Dictionary<EnumDespawnReason, long> DespawnedByReason = [];

    /// <summary>
    /// Despawn events that only mean "the client stopped tracking it": the entity stays
    /// in the world and stays in the capture. Counted to be able to show the difference —
    /// these are not removals, and a report that mixed them with real ones would say the
    /// capture tracks a thousand dead mobs.
    /// </summary>
    private static readonly Dictionary<EnumDespawnReason, long> KeptByReason = [];

    /// <summary>
    /// Despawns of entities that were never written in this session, so no record was
    /// made. They are usually entities captured in an earlier session: the client only
    /// despawns what it tracks, and a tracked entity is written on load — but a skipped
    /// one (no type code, serialization failure) leaves an old record in the capture that
    /// this despawn should have removed. Non-zero means exactly that.
    /// </summary>
    private static long _despawnMissed;

    /// <summary>How many distinct entities of this session have been written at least once.</summary>
    public static int Tracked => Written.Count;

    public static long Captured => _captured;
    public static long Skipped => _skipped;
    public static long Failed => _failed;

    public static long Despawned
    {
        get
        {
            long total = 0;
            foreach (long count in DespawnedByReason.Values) total += count;
            return total;
        }
    }

    public static long DespawnMissed => _despawnMissed;

    public static void Install(ICoreClientAPI capi, ILogger logger)
    {
        Uninstall();
        _capi = capi;
        _logger = logger;
        capi.Event.OnEntitySpawn += OnEntitySpawn;
        capi.Event.OnEntityLoaded += OnEntityLoaded;
        capi.Event.OnEntityDespawn += OnEntityDespawn;
    }

    public static void Uninstall()
    {
        var capi = _capi;
        _capi = null;
        if (capi == null) return;

        try
        {
            capi.Event.OnEntitySpawn -= OnEntitySpawn;
            capi.Event.OnEntityLoaded -= OnEntityLoaded;
            capi.Event.OnEntityDespawn -= OnEntityDespawn;
        }
        catch (Exception)
        {
            // On exit the engine may already be partially torn down.
        }
    }

    /// <summary>
    /// Forget the entities of the previous world: their ids belong to another server,
    /// and keeping them would make a despawn from the new session remove a foreign entity.
    /// </summary>
    public static void ResetSession()
    {
        _sweep = null;
        _sweepIndex = 0;
        _lastSweepMs = 0;
        Written.Clear();
        _captured = _skipped = _failed = 0;
        DespawnedByReason.Clear();
        KeptByReason.Clear();
        _despawnMissed = 0;
    }

    private static void OnEntitySpawn(Entity entity) => Capture(entity);

    private static void OnEntityLoaded(Entity entity) => Capture(entity);

    private static void OnEntityDespawn(Entity entity, EntityDespawnData reasonData)
    {
        if (!CapturePipeline.Active) return;
        if (KeepsEntity(reasonData.Reason))
        {
            KeptByReason[reasonData.Reason] = KeptByReason.GetValueOrDefault(reasonData.Reason) + 1;
            return;
        }

        // Only entities we actually put into the capture are removed: a despawn for a
        // player or for an entity captured before the mod was installed would otherwise
        // delete an entity of an earlier session with the same id.
        long id = entity.EntityId;
        if (!Written.Contains(id))
        {
            _despawnMissed++;
            return;
        }

        Written.Remove(id);
        DespawnedByReason[reasonData.Reason] = DespawnedByReason.GetValueOrDefault(reasonData.Reason) + 1;
        byte reason = (byte)reasonData.Reason;
        CapturePipeline.Enqueue(() => CapturePipeline.Write(
            CaptureRecordType.EntityDespawn,
            CapturePayloadCodec.Encode(new EntityDespawnPayload { EntityId = id, Reason = reason })));
    }

    /// <summary>
    /// Whether the entity still exists on the server after this despawn. "Out of range",
    /// "chunk unloaded" and "the last player left" only mean that the client stopped
    /// tracking it — the entity itself stays in the world, and in the assembled world it
    /// has to stay as well.
    /// </summary>
    private static bool KeepsEntity(EnumDespawnReason reason)
        => reason is EnumDespawnReason.OutOfRange or EnumDespawnReason.Unload or EnumDespawnReason.Disconnect;

    /// <summary>
    /// "removed 7 773 (Death 4 000, PickedUp 3 000), kept 12 000 (out of range 11 500, unloaded 400)".
    /// Shown in the status: without it the entity counters only ever grow, and the capture
    /// looks like it never removes anything.
    /// </summary>
    public static string DespawnStatusText()
    {
        var parts = new List<string>();
        if (DespawnedByReason.Count > 0) parts.Add("removed " + Describe(DespawnedByReason));
        if (KeptByReason.Count > 0) parts.Add("kept " + Describe(KeptByReason));
        if (_despawnMissed > 0) parts.Add($"not tracked here {_despawnMissed}");
        return string.Join(", ", parts);
    }

    private static string Describe(Dictionary<EnumDespawnReason, long> counts)
    {
        long total = 0;
        foreach (long count in counts.Values) total += count;

        var names = counts.OrderByDescending(pair => pair.Value)
            .Select(pair => $"{pair.Key} {pair.Value}");
        return $"{total} ({string.Join(", ", names)})";
    }

    /// <summary>
    /// Refresh pass: re-write the state of the entities that are loaded right now.
    /// Entities that appeared before the mod or its events were active get picked up here too.
    /// </summary>
    public static void Tick(long now)
    {
        var capi = _capi;
        if (capi == null || !CapturePipeline.Active)
        {
            _sweep = null;
            _sweepIndex = 0;
            return;
        }

        var config = FullCaptureModSystem.ActiveConfig;
        if (!config.CaptureEntities || !config.CaptureChunks) return;

        if (_sweep == null)
        {
            if (config.CaptureEntityRefreshIntervalMs <= 0) return;
            if (now - _lastSweepMs < config.CaptureEntityRefreshIntervalMs) return;

            _lastSweepMs = now;
            _sweep = [.. capi.World.LoadedEntities.Values];
            _sweepIndex = 0;
        }

        int budget = Math.Max(1, config.CaptureEntitiesPerTick);
        int done = 0;
        while (_sweepIndex < _sweep.Count && done < budget)
        {
            Capture(_sweep[_sweepIndex++]);
            done++;
        }

        if (_sweepIndex >= _sweep.Count)
        {
            _sweep = null;
            _sweepIndex = 0;
        }
    }

    private static void Capture(Entity entity)
    {
        // Paused (singleplayer with OnlyOnMultiplayer) is checked first: serialization
        // walks the live object, and there is no point doing that for a dropped record.
        if (!CapturePipeline.Active) return;

        var config = FullCaptureModSystem.ActiveConfig;
        if (!config.CaptureEntities || !config.CaptureChunks) return;

        // The same rule the server applies when saving a chunk (Entity.StoreWithChunk),
        // plus an explicit player check: a player lives in playerdata, not in a chunk.
        if (entity is EntityPlayer || !entity.StoreWithChunk) return;

        // Code is what the entity is resolved by on load (world.GetEntityType(entity.Code)),
        // and Entity.ToBytes logs through World when it is null — which a bare entity does not have.
        if (entity.Code == null)
        {
            CountSkipped("without a type code (the entity was not initialized yet)");
            return;
        }

        string? classname = ResolveClassname(entity);
        if (string.IsNullOrEmpty(classname))
        {
            CountSkipped("without a class name (the entity type is not registered)");
            return;
        }

        byte[] data;
        try
        {
            data = EntitySaveData.Extract(entity);
        }
        catch (Exception e)
        {
            _failed++;
            if (_failed <= 3)
            {
                // A single exotic entity must not abort the capture: the entry is skipped,
                // the rest of the world is written.
                _logger?.Warning("Entity {0} ({1}) was not serialized and is skipped: {2}",
                    entity.EntityId, classname, e.Message);
            }
            return;
        }

        var payload = new EntityPayload
        {
            EntityId = entity.EntityId,
            Classname = classname,
            X = entity.Pos.X,
            Y = entity.Pos.Y,
            Z = entity.Pos.Z,
            SaveData = data
        };

        CapturePipeline.Enqueue(() => CapturePipeline.Write(
            CaptureRecordType.Entity, CapturePayloadCodec.Encode(payload)));

        _captured++;
        Written.Add(entity.EntityId);
    }

    /// <summary>
    /// Class name by the CLR type of the entity. The name comes from the entity type,
    /// because that is the name the entity was created with on this side as well
    /// (<c>ClassRegistry.CreateEntity(entityType)</c> resolves <c>entityType.Class</c>).
    /// </summary>
    private static string? ResolveClassname(Entity entity)
    {
        var type = entity.GetType();
        if (Classnames.TryGetValue(type, out string? cached)) return cached;

        string? name = EntitySaveData.ClassnameOf(entity);
        Classnames[type] = name;
        if (string.IsNullOrEmpty(name))
        {
            _logger?.Warning("Entity type {0} has no class name — such entities are not captured.", type.Name);
        }
        return name;
    }

    private static void CountSkipped(string reason)
    {
        _skipped++;
        if (_skipped <= 3) _logger?.Warning("Entity skipped: " + reason);
    }
}
