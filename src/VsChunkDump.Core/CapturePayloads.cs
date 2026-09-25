using System.Text;

namespace VsChunkDump.Core;

/// <summary>
/// Data of a single chunk captured from a network packet.
/// A deliberately flat structure without game types: thanks to that, encoding is
/// tested offline, and the mod and the collector merely map game packets onto it.
/// </summary>
public sealed class ChunkPayload
{
    public int X, Y, Z;
    public int Compver;
    public bool Empty;

    public byte[]? Blocks;
    public byte[]? Light;
    public byte[]? LightSat;
    public byte[]? Liquids;
    public byte[]? Moddata;

    public int[] LightPositions = [];
    public int[] DecorsPos = [];
    public int[] DecorsIds = [];

    /// <summary>
    /// Block entities of the chunk: without them every chiseled block is lost (its shape lives
    /// in the block entity), along with chests, machines and anything else drawn not by the block itself.
    /// The field was added after the others, so older records do not have it.
    /// </summary>
    public List<BlockEntityPayload> BlockEntities = [];
}

/// <summary>
/// One block entity as it travels over the network: the class name and the
/// serialized <c>TreeAttribute</c> (the same bytes the game writes into the savegame).
/// </summary>
public sealed class BlockEntityPayload
{
    public string Classname = "";
    public int X, Y, Z;
    public byte[] Data = [];
}

/// <summary>
/// Block entity updates that arrive in a separate packet (id 48) — this is how
/// the server sends an interaction rollback. The records are applied to chunks by position.
/// </summary>
public sealed class BlockEntityUpdatePayload
{
    public List<BlockEntityPayload> BlockEntities = [];
}

/// <summary>World parameters from the server identification packet.</summary>
public sealed class WorldInfoPayload
{
    public string? GameVersion;
    public string? ServerName;
    public string? SavegameIdentifier;
    public int MapSizeX, MapSizeY, MapSizeZ;
    public int Seed;
    public int RequireRemapping;
    public List<ModEntry> Mods = [];

    public readonly record struct ModEntry(string Id, string Version);
}

public sealed class LevelInitPayload
{
    public int ServerChunkSize;
    public int ServerMapChunkSize;
    public int ServerMapRegionSize;
    public int MaxViewDistance;
}

public sealed class WorldMetaPayload
{
    public int SeaLevel;
    public int SunBrightness;
    public int[] BlockLightLevels = [];
    public int[] SunLightLevels = [];

    /// <summary>
    /// Serialized world configuration (TreeAttribute). The packet carries exactly the same
    /// bytes that the game writes into SaveGame.WorldConfigBytes, so a local save can be
    /// assembled from them without a template world.
    /// </summary>
    public byte[]? WorldConfiguration;
}

/// <summary>Block registry 'id → code'. Needed for gamedata.ModData["BlockIDs"].</summary>
public sealed class BlockRegistryPayload
{
    public List<Entry> Blocks = [];

    public readonly record struct Entry(int BlockId, string Code);
}

/// <summary>Column heightmap. The bytes are as in the packet (little-endian ushort).</summary>
public sealed class MapChunkPayload
{
    public int ChunkX, ChunkZ, Ymax;
    public byte[]? RainHeightMap;
    public byte[]? TerrainHeightMap;
}

/// <summary>
/// Encoding of capture record bodies.
///
/// Why we do not re-serialize the game's protobuf packets: the game's generators serialize
/// nested messages through SerializeWithSize, which writes a CACHED size field
/// and checks it against the actual byte count. When an incoming packet is parsed, that
/// field is not filled in, so re-serializing fails with
/// 'Sizing mismatch: 0 != N' for everything that has nested messages (chunks with
/// entities, the block registry, the server identification).
///
/// That is why we write only the fields that are really needed, in our own explicit format.
/// </summary>
public static class CapturePayloadCodec
{
    // ------------------------------------------------------------------ chunk

    public static byte[] Encode(ChunkPayload c)
    {
        using var ms = new MemoryStream(64 * 1024);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(c.X);
        w.Write(c.Y);
        w.Write(c.Z);
        w.Write(c.Compver);
        w.Write(c.Empty);
        WriteBytes(w, c.Blocks);
        WriteBytes(w, c.Light);
        WriteBytes(w, c.LightSat);
        WriteBytes(w, c.Liquids);
        WriteBytes(w, c.Moddata);
        WriteInts(w, c.LightPositions);
        WriteInts(w, c.DecorsPos);
        WriteInts(w, c.DecorsIds);
        WriteBlockEntities(w, c.BlockEntities);
        w.Flush();
        return ms.ToArray();
    }

    public static ChunkPayload DecodeChunk(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var c = new ChunkPayload
        {
            X = r.ReadInt32(),
            Y = r.ReadInt32(),
            Z = r.ReadInt32(),
            Compver = r.ReadInt32(),
            Empty = r.ReadBoolean()
        };
        c.Blocks = ReadBytes(r);
        c.Light = ReadBytes(r);
        c.LightSat = ReadBytes(r);
        c.Liquids = ReadBytes(r);
        c.Moddata = ReadBytes(r);
        c.LightPositions = ReadInts(r);
        c.DecorsPos = ReadInts(r);
        c.DecorsIds = ReadInts(r);
        c.BlockEntities = ReadOptionalBlockEntities(r);
        return c;
    }

    // ----------------------------------------------------------- block entities

    public static byte[] Encode(BlockEntityUpdatePayload p)
    {
        using var ms = new MemoryStream(8192);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        WriteBlockEntities(w, p.BlockEntities);
        w.Flush();
        return ms.ToArray();
    }

    public static BlockEntityUpdatePayload DecodeBlockEntityUpdate(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return new BlockEntityUpdatePayload { BlockEntities = ReadBlockEntities(r) };
    }

    // ------------------------------------------------- server identification

    public static byte[] Encode(WorldInfoPayload w2)
    {
        using var ms = new MemoryStream(4096);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(w2.GameVersion ?? "");
        w.Write(w2.ServerName ?? "");
        w.Write(w2.SavegameIdentifier ?? "");
        w.Write(w2.MapSizeX);
        w.Write(w2.MapSizeY);
        w.Write(w2.MapSizeZ);
        w.Write(w2.Seed);
        w.Write(w2.RequireRemapping);
        w.Write(w2.Mods.Count);
        foreach (var mod in w2.Mods)
        {
            w.Write(mod.Id);
            w.Write(mod.Version);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static WorldInfoPayload DecodeWorldInfo(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var w = new WorldInfoPayload
        {
            GameVersion = r.ReadString(),
            ServerName = r.ReadString(),
            SavegameIdentifier = r.ReadString(),
            MapSizeX = r.ReadInt32(),
            MapSizeY = r.ReadInt32(),
            MapSizeZ = r.ReadInt32(),
            Seed = r.ReadInt32(),
            RequireRemapping = r.ReadInt32()
        };
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string id = r.ReadString();
            string version = r.ReadString();
            w.Mods.Add(new WorldInfoPayload.ModEntry(id, version));
        }
        return w;
    }

    // ------------------------------------------------------ params and meta

    public static byte[] Encode(LevelInitPayload p)
    {
        using var ms = new MemoryStream(32);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(p.ServerChunkSize);
        w.Write(p.ServerMapChunkSize);
        w.Write(p.ServerMapRegionSize);
        w.Write(p.MaxViewDistance);
        w.Flush();
        return ms.ToArray();
    }

    public static LevelInitPayload DecodeLevelInit(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return new LevelInitPayload
        {
            ServerChunkSize = r.ReadInt32(),
            ServerMapChunkSize = r.ReadInt32(),
            ServerMapRegionSize = r.ReadInt32(),
            MaxViewDistance = r.ReadInt32()
        };
    }

    public static byte[] Encode(WorldMetaPayload p)
    {
        using var ms = new MemoryStream(4096);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(p.SeaLevel);
        w.Write(p.SunBrightness);
        WriteInts(w, p.BlockLightLevels);
        WriteInts(w, p.SunLightLevels);
        WriteBytes(w, p.WorldConfiguration);
        w.Flush();
        return ms.ToArray();
    }

    public static WorldMetaPayload DecodeWorldMeta(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return new WorldMetaPayload
        {
            SeaLevel = r.ReadInt32(),
            SunBrightness = r.ReadInt32(),
            BlockLightLevels = ReadInts(r),
            SunLightLevels = ReadInts(r),
            // The field was added later, so older records may not have it.
            WorldConfiguration = ReadOptionalBytes(r)
        };
    }

    // ----------------------------------------------------------- block registry

    public static byte[] Encode(BlockRegistryPayload p)
    {
        using var ms = new MemoryStream(64 * 1024);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(p.Blocks.Count);
        foreach (var block in p.Blocks)
        {
            w.Write(block.BlockId);
            w.Write(block.Code);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static BlockRegistryPayload DecodeBlockRegistry(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        int count = r.ReadInt32();
        var registry = new BlockRegistryPayload();
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadInt32();
            string code = r.ReadString();
            registry.Blocks.Add(new BlockRegistryPayload.Entry(id, code));
        }
        return registry;
    }

    // ------------------------------------------------------------ heightmap

    public static byte[] Encode(MapChunkPayload p)
    {
        using var ms = new MemoryStream(8192);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(p.ChunkX);
        w.Write(p.ChunkZ);
        w.Write(p.Ymax);
        WriteBytes(w, p.RainHeightMap);
        WriteBytes(w, p.TerrainHeightMap);
        w.Flush();
        return ms.ToArray();
    }

    public static MapChunkPayload DecodeMapChunk(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return new MapChunkPayload
        {
            ChunkX = r.ReadInt32(),
            ChunkZ = r.ReadInt32(),
            Ymax = r.ReadInt32(),
            RainHeightMap = ReadBytes(r),
            TerrainHeightMap = ReadBytes(r)
        };
    }

    // ---------------------------------------------------------------- utilities

    private static void WriteBytes(BinaryWriter w, byte[]? data)
    {
        if (data == null)
        {
            w.Write(-1);
            return;
        }
        w.Write(data.Length);
        w.Write(data);
    }

    private static byte[]? ReadBytes(BinaryReader r)
    {
        int length = r.ReadInt32();
        return length < 0 ? null : r.ReadBytes(length);
    }

    /// <summary>
    /// Optional field at the end of a record. The format only evolves by appending
    /// fields to the tail, so missing data means 'the field was not written', not
    /// a corrupt file (integrity is protected by the CRC anyway).
    /// </summary>
    private static byte[]? ReadOptionalBytes(BinaryReader r)
    {
        if (r.BaseStream.Position >= r.BaseStream.Length) return null;
        return ReadBytes(r);
    }

    private static void WriteInts(BinaryWriter w, int[]? values)
    {
        if (values == null)
        {
            w.Write(-1);
            return;
        }
        w.Write(values.Length);
        foreach (int v in values) w.Write(v);
    }

    private static int[] ReadInts(BinaryReader r)
    {
        int length = r.ReadInt32();
        if (length < 0) return [];
        var values = new int[length];
        for (int i = 0; i < length; i++) values[i] = r.ReadInt32();
        return values;
    }

    private static void WriteBlockEntities(BinaryWriter w, List<BlockEntityPayload>? blockEntities)
    {
        if (blockEntities == null)
        {
            w.Write(-1);
            return;
        }
        w.Write(blockEntities.Count);
        foreach (var be in blockEntities)
        {
            w.Write(be.Classname);
            w.Write(be.X);
            w.Write(be.Y);
            w.Write(be.Z);
            WriteBytes(w, be.Data);
        }
    }

    /// <summary>
    /// Record tail: captures taken before block entities existed do not have the field.
    /// </summary>
    private static List<BlockEntityPayload> ReadOptionalBlockEntities(BinaryReader r)
        => r.BaseStream.Position >= r.BaseStream.Length ? [] : ReadBlockEntities(r);

    private static List<BlockEntityPayload> ReadBlockEntities(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0) return [];
        var list = new List<BlockEntityPayload>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new BlockEntityPayload
            {
                Classname = r.ReadString(),
                X = r.ReadInt32(),
                Y = r.ReadInt32(),
                Z = r.ReadInt32(),
                Data = ReadBytes(r) ?? []
            });
        }
        return list;
    }
}
