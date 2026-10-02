using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

/// <summary>
/// Entities in the capture. They are what makes mobs, dropped items and item frames
/// appear in the assembled world: a chunk row keeps them inside itself, and the client
/// never receives them in that form, so the mod takes them from the live objects.
/// </summary>
public class EntityPayloadTests
{
    private static EntityPayload Sample(long id, string classname, double x, double y, double z, params byte[] data)
        => new()
        {
            EntityId = id,
            Classname = classname,
            X = x,
            Y = y,
            Z = z,
            SaveData = data
        };

    private static CaptureManifest Manifest() => new() { GameVersion = "1.22.7" };

    [Fact]
    public void EntityRecordRoundTrips()
    {
        var entity = Sample(1234567890123, "EntityAgent", 12.5, 65.25, -7.75, 1, 2, 3, 4, 5);

        var decoded = CapturePayloadCodec.DecodeEntity(CapturePayloadCodec.Encode(entity));

        Assert.Equal(entity.EntityId, decoded.EntityId);
        Assert.Equal("EntityAgent", decoded.Classname);
        Assert.Equal(12.5, decoded.X);
        Assert.Equal(65.25, decoded.Y);
        Assert.Equal(-7.75, decoded.Z);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, decoded.SaveData);
    }

    [Fact]
    public void EntityRecordSurvivesAnEmptyBody()
    {
        // An entity whose class serialized to nothing: the record must still carry the
        // id and position, the builder is the one that drops it (EntityDropped).
        var decoded = CapturePayloadCodec.DecodeEntity(
            CapturePayloadCodec.Encode(Sample(7, "EntityItem", 0, 0, 0)));

        Assert.Equal(7, decoded.EntityId);
        Assert.Empty(decoded.SaveData);
    }

    [Fact]
    public void DespawnRecordRoundTrips()
    {
        var decoded = CapturePayloadCodec.DecodeEntityDespawn(
            CapturePayloadCodec.Encode(new EntityDespawnPayload { EntityId = -9007199254740993 }));

        Assert.Equal(-9007199254740993, decoded.EntityId);
    }

    [Fact]
    public void DespawnRecordKeepsTheReason()
    {
        var decoded = CapturePayloadCodec.DecodeEntityDespawn(
            CapturePayloadCodec.Encode(new EntityDespawnPayload { EntityId = 5, Reason = 3 }));

        Assert.Equal(5, decoded.EntityId);
        Assert.Equal(3, decoded.Reason);
    }

    [Fact]
    public void DespawnRecordWrittenWithoutAReasonStillDecodes()
    {
        // The reason was added later and is optional: records of the older 8-byte shape
        // stay readable, and the reason is then simply unknown.
        byte[] payload = BitConverter.GetBytes(42L);

        var decoded = CapturePayloadCodec.DecodeEntityDespawn(payload);

        Assert.Equal(42, decoded.EntityId);
        Assert.Equal(EntityDespawnPayload.Unknown, decoded.Reason);
    }

    [Fact]
    public void EntityRecordTypesDoNotRepeatTheExistingOnes()
    {
        var types = Enum.GetValues<CaptureRecordType>().Cast<ushort>().ToList();

        Assert.Equal(types.Count, types.Distinct().Count());
        Assert.Equal((ushort)9, (ushort)CaptureRecordType.Entity);
        Assert.Equal((ushort)10, (ushort)CaptureRecordType.EntityDespawn);
    }

    [Fact]
    public void LastEntityRecordWins()
    {
        using var dir = new TempDir("entities");
        Write(dir, [
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(42, "EntityAgent", 1, 2, 3, 0xAA))),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(42, "EntityAgent", 10, 20, 30, 0xBB)))
        ]);

        var model = CaptureModel.Load(dir.Path);

        var entity = Assert.Single(model.Entities).Value;
        Assert.Equal(42, entity.EntityId);
        Assert.Equal(10, entity.X);
        Assert.Equal(new byte[] { 0xBB }, entity.SaveData);
    }

    [Fact]
    public void DespawnRemovesEntity()
    {
        using var dir = new TempDir("entities");
        Write(dir, [
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(1, "EntityItem", 0, 0, 0, 1))),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(2, "EntityItem", 0, 0, 0, 2))),
            (CaptureRecordType.EntityDespawn, CapturePayloadCodec.Encode(new EntityDespawnPayload { EntityId = 1 }))
        ]);

        var model = CaptureModel.Load(dir.Path);

        Assert.Equal(2, model.Entities[2].EntityId);
        Assert.False(model.Entities.ContainsKey(1));
    }

    [Fact]
    public void DespawnOfAnUnknownEntityIsIgnored()
    {
        using var dir = new TempDir("entities");
        Write(dir, [
            (CaptureRecordType.EntityDespawn, CapturePayloadCodec.Encode(new EntityDespawnPayload { EntityId = 999 }))
        ]);

        var model = CaptureModel.Load(dir.Path);

        Assert.Empty(model.Entities);
    }

    [Fact]
    public void DespawnsAreCountedWithTheirReasons()
    {
        using var dir = new TempDir("entities");
        Write(dir, [
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(1, "EntityAgent", 0, 0, 0, 1))),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(2, "EntityAgent", 0, 0, 0, 2))),
            (CaptureRecordType.EntityDespawn,
                CapturePayloadCodec.Encode(new EntityDespawnPayload { EntityId = 1, Reason = 0 })),
            (CaptureRecordType.EntityDespawn,
                CapturePayloadCodec.Encode(new EntityDespawnPayload { EntityId = 2, Reason = 4 }))
        ]);

        var model = CaptureModel.Load(dir.Path);

        Assert.Empty(model.Entities);
        Assert.Equal(2, model.Despawns);
        Assert.Equal(1, model.DespawnReasons[0]);
        Assert.Equal(1, model.DespawnReasons[4]);
    }

    /// <summary>
    /// A despawn record only exists while the client is tracking the entity, so how many
    /// connections ago an entity was last mentioned is the only age the capture has. An
    /// entity the model knows from two connections back may be long dead on the server.
    /// </summary>
    [Fact]
    public void EntitiesKnowWhichConnectionLastMentionedThem()
    {
        using var dir = new TempDir("entities");
        Write(dir, [
            (CaptureRecordType.ServerIdentification, Identification("first")),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(1, "EntityAgent", 0, 0, 0, 1))),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(2, "EntityAgent", 1, 0, 1, 1))),
            (CaptureRecordType.ServerIdentification, Identification("second")),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(1, "EntityAgent", 2, 0, 2, 2))),
            (CaptureRecordType.EntityDespawn,
                CapturePayloadCodec.Encode(new EntityDespawnPayload { EntityId = 2 })),
            (CaptureRecordType.ServerIdentification, Identification("third"))
        ]);

        var model = CaptureModel.Load(dir.Path);

        Assert.Equal(3, model.Connections);
        Assert.Equal(2, Assert.Single(model.Entities).Value.LastConnection);
        Assert.Equal(new SortedDictionary<int, int> { [1] = 1 }, model.EntitiesByAge());
    }

    [Fact]
    public void AgeBoundLeavesOutOnlyEntitiesNotMentionedForTooLong()
    {
        using var dir = new TempDir("entities");
        Write(dir, [
            (CaptureRecordType.ServerIdentification, Identification("first")),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(1, "EntityAgent", 0, 0, 0, 1))),
            (CaptureRecordType.ServerIdentification, Identification("second")),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(2, "EntityAgent", 0, 0, 0, 1))),
            (CaptureRecordType.ServerIdentification, Identification("third")),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(3, "EntityAgent", 0, 0, 0, 1)))
        ]);

        var model = CaptureModel.Load(dir.Path);

        // There are no chunks in the capture at all, so the chunk rule has nothing to
        // compare against and only the optional age bound can cut.
        var all = model.GroupEntitiesByChunk(-1, out var keptScope);
        Assert.Equal(3, keptScope.Kept);
        Assert.Equal(0, keptScope.Superseded);
        Assert.Equal(0, keptScope.TooOld);

        var bounded = model.GroupEntitiesByChunk(1, out var scope);
        Assert.Equal([2L, 3L], bounded.Values.SelectMany(list => list).Select(e => e.EntityId).Order());
        Assert.Equal(1, scope.TooOld);
        Assert.Equal(0, scope.Superseded);
    }

    [Fact]
    public void TheRulesKeepEntitiesWhoseAgeIsUnknown()
    {
        // A capture with no identification records at all: every entity has an unknown
        // age, and throwing those away would silently build an empty world.
        var model = new CaptureModel();
        Add(model, 1, 12.5, 65.25, -7.75);

        var byChunk = model.GroupEntitiesByChunk(1, out var scope);

        Assert.Single(Assert.Single(byChunk).Value);
        Assert.Equal(1, scope.Kept);
        Assert.Equal(0, scope.Superseded);
        Assert.Equal(0, scope.TooOld);
    }

    [Fact]
    public void CaptureWithoutEntitiesStillLoads()
    {
        using var dir = new TempDir("entities");
        Write(dir, [
            (CaptureRecordType.Chunk, CapturePayloadCodec.Encode(new ChunkPayload { X = 1, Y = 2, Z = 3, Blocks = [1] }))
        ]);

        var model = CaptureModel.Load(dir.Path);

        Assert.Single(model.Chunks);
        Assert.Empty(model.Entities);
        Assert.Empty(model.GroupEntitiesByChunk());
    }

    [Fact]
    public void EntitiesAreGroupedByChunkSection()
    {
        var model = new CaptureModel();
        Add(model, 1, 12.5, 65.25, -7.75);      // (0, 2, -1)
        Add(model, 2, -0.5, 0.0, -32.0);        // (-1, 0, -1)
        Add(model, 3, -32.5, 31.99, 31.99);     // (-2, 0, 0)
        Add(model, 4, 32.0, 96.0, 64.0);        // (1, 3, 2)

        var byChunk = model.GroupEntitiesByChunk();

        Assert.Equal([1L], Ids(byChunk, (0, 2, -1)));
        Assert.Equal([2L], Ids(byChunk, (-1, 0, -1)));
        Assert.Equal([3L], Ids(byChunk, (-2, 0, 0)));
        Assert.Equal([4L], Ids(byChunk, (1, 3, 2)));
        Assert.Equal(4, byChunk.Count);

        static long[] Ids(Dictionary<(int X, int Y, int Z), List<EntityPayload>> byChunk, (int X, int Y, int Z) key)
            => byChunk[key].Select(e => e.EntityId).Order().ToArray();
    }

    [Fact]
    public void EntitiesWithABrokenPositionAreNotPlaced()
    {
        var model = new CaptureModel();
        Add(model, 1, double.NaN, 64, 0);
        Add(model, 2, 0, 64, double.PositiveInfinity);
        Add(model, 3, 0, 64, 0);

        var byChunk = model.GroupEntitiesByChunk();

        Assert.Equal([3L], Assert.Single(byChunk).Value.Select(e => e.EntityId).ToArray());
    }

    /// <summary>
    /// The entity set is scoped per chunk, not to the newest connection. An entity is left
    /// out when its own chunk was received again later, in a connection that recorded
    /// entities, and the entity was not among them. A chunk that was not received again
    /// says nothing about its entities, so they stay: the capture's silence about a place
    /// the client never returned to is not evidence that the place is empty.
    /// </summary>
    [Fact]
    public void EntityScopeIsTheLastLookAtItsOwnChunk()
    {
        using var dir = new TempDir("entities");
        Write(dir, [
            (CaptureRecordType.ServerIdentification, Identification("first")),
            (CaptureRecordType.Chunk, Chunk(0, 0, 0)),
            (CaptureRecordType.Chunk, Chunk(1, 0, 0)),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(1, "EntityAgent", 0.5, 0.5, 0.5, 1))),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(2, "EntityAgent", 32.5, 0.5, 0.5, 1))),
            (CaptureRecordType.ServerIdentification, Identification("second")),
            (CaptureRecordType.Chunk, Chunk(0, 0, 0)),   // only this place is looked at again
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(3, "EntityAgent", 0.5, 0.5, 0.5, 1)))
        ]);

        var model = CaptureModel.Load(dir.Path);

        Assert.Equal(2, model.Connections);
        Assert.Equal(2, model.ConnectionsWithEntities);
        Assert.Equal(2, model.SupersedeConnections[(0, 0, 0)]);
        Assert.Equal(1, model.SupersedeConnections[(1, 0, 0)]);

        var byChunk = model.GroupEntitiesByChunk(-1, out var scope);

        // 1 stood in (0,0,0) and was not there when that chunk came again; 3 is the entity
        // that was seen instead. 2 stands in a chunk nobody returned to and must stay.
        Assert.Equal([2L, 3L], byChunk.Values.SelectMany(list => list).Select(e => e.EntityId).Order());
        Assert.Equal(1, scope.Superseded);
        Assert.Equal(2, scope.Kept);
    }

    /// <summary>
    /// Receiving a chunk is not evidence on its own: a connection can load chunks and record
    /// no entity at all — the player reconnected and left before anything was sent. Such a
    /// connection says nothing about the entity set, so it supersedes nothing.
    /// </summary>
    [Fact]
    public void AConnectionThatRecordedNoEntitySupersedesNothing()
    {
        using var dir = new TempDir("entities");
        Write(dir, [
            (CaptureRecordType.ServerIdentification, Identification("first")),
            (CaptureRecordType.Chunk, Chunk(0, 0, 0)),
            (CaptureRecordType.Entity, CapturePayloadCodec.Encode(Sample(1, "EntityAgent", 0.5, 0.5, 0.5, 1))),
            (CaptureRecordType.ServerIdentification, Identification("second")),
            (CaptureRecordType.Chunk, Chunk(0, 0, 0))
        ]);

        var model = CaptureModel.Load(dir.Path);

        Assert.Equal(2, model.Connections);
        Assert.Equal(1, model.ConnectionsWithEntities);
        Assert.Equal(2, model.ChunkConnections[(0, 0, 0)]);      // the chunk was received again …
        Assert.Equal(1, model.SupersedeConnections[(0, 0, 0)]);  // … but that connection said nothing
        Assert.Equal(1, model.ChunksWithoutNewerEntityInfo);

        var byChunk = model.GroupEntitiesByChunk(-1, out var scope);
        Assert.Equal([1L], byChunk.Values.SelectMany(list => list).Select(e => e.EntityId));
        Assert.Equal(0, scope.Superseded);
    }

    private static void Add(CaptureModel model, long id, double x, double y, double z)
        => model.Entities[id] = Sample(id, "EntityAgent", x, y, z, 1);

    private static byte[] Identification(string serverName)
        => CapturePayloadCodec.Encode(new WorldInfoPayload { ServerName = serverName });

    /// <summary>A chunk section record: the builder needs the chunks themselves, and the rule needs them to tell "looked at again" from "never returned to".</summary>
    private static byte[] Chunk(int x, int y, int z)
        => CapturePayloadCodec.Encode(new ChunkPayload { X = x, Y = y, Z = z, Blocks = [1] });

    private static void Write(TempDir dir, (CaptureRecordType Type, byte[] Payload)[] records)
    {
        using var writer = new CaptureWriter(dir.Path, Manifest());
        foreach (var (type, payload) in records) writer.Write(type, payload);
    }
}
