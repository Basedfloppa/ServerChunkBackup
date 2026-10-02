using Vintagestory.Common;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Mapping of game packets to flat capture structures.
///
/// Kept separate from the patch because the offline writer uses the same code
/// (the file is linked as source) — so the conversion is not duplicated and is
/// covered by a self-test.
/// </summary>
public static class PacketMapping
{
    public static ChunkPayload ToPayload(Packet_ServerChunk p) => new()
    {
        X = p.X,
        Y = p.Y,
        Z = p.Z,
        Compver = p.Compver,
        Empty = p.Empty > 0,
        Blocks = p.Blocks,
        Light = p.Light,
        LightSat = p.LightSat,
        Liquids = p.Liquids,
        Moddata = p.Moddata,
        LightPositions = TakeInts(p.LightPositions, p.LightPositionsCount),
        DecorsPos = TakeInts(p.DecorsPos, p.DecorsPosCount),
        DecorsIds = TakeInts(p.DecorsIds, p.DecorsIdsCount),
        BlockEntities = TakeBlockEntities(p.BlockEntities, p.BlockEntitiesCount)
    };

    /// <summary>
    /// Block entities arrive both inside a chunk (the shape of chiseled blocks,
    /// the contents of chests and machines) and as a separate update packet.
    /// </summary>
    public static BlockEntityUpdatePayload ToPayload(Packet_BlockEntities p) => new()
    {
        BlockEntities = TakeBlockEntities(p.BlockEntitites, p.BlockEntititesCount)
    };

    private static List<BlockEntityPayload> TakeBlockEntities(Packet_BlockEntity[]? values, int count)
    {
        if (values == null) return [];
        int n = count > 0 && count <= values.Length ? count : values.Length;
        var list = new List<BlockEntityPayload>(n);
        for (int i = 0; i < n; i++)
        {
            var be = values[i];
            if (be?.Data == null || be.Data.Length == 0) continue;
            list.Add(new BlockEntityPayload
            {
                Classname = be.Classname ?? "",
                X = be.PosX,
                Y = be.PosY,
                Z = be.PosZ,
                Data = be.Data
            });
        }
        return list;
    }

    public static WorldInfoPayload ToPayload(Packet_ServerIdentification p)
    {
        var payload = new WorldInfoPayload
        {
            GameVersion = p.GameVersion,
            ServerName = p.ServerName,
            SavegameIdentifier = p.SavegameIdentifier,
            MapSizeX = p.MapSizeX,
            MapSizeY = p.MapSizeY,
            MapSizeZ = p.MapSizeZ,
            Seed = p.Seed,
            RequireRemapping = p.RequireRemapping
        };

        if (p.Mods != null)
        {
            int count = p.ModsCount > 0 ? p.ModsCount : p.Mods.Length;
            for (int i = 0; i < count && i < p.Mods.Length; i++)
            {
                var mod = p.Mods[i];
                if (mod == null) continue;
                payload.Mods.Add(new WorldInfoPayload.ModEntry(mod.Modid ?? "", mod.Version ?? ""));
            }
        }
        return payload;
    }

    /// <summary>
    /// Calendar settings from the server's calendar packet.
    ///
    /// The packet carries its floats packed into ints (<c>CollectibleNet</c>), so they
    /// are unpacked with the same helpers the game uses — reading the raw ints as
    /// floats would silently produce nonsense (HoursPerDay would become 240000).
    ///
    /// The packet's own <c>TotalSeconds</c> is deliberately not taken: the current clock
    /// comes from the client's calendar, which was set from this very packet and then
    /// advanced locally, so it is the same clock read later (see WorldStateCapture).
    /// <c>TotalSecondsStart</c> never advances, so the packet's value is the one to keep.
    /// </summary>
    public static CalendarSettings ToCalendarSettings(Packet_ServerCalendar p)
    {
        var settings = new CalendarSettings
        {
            TotalSecondsStart = p.TotalSecondsStart,
            HoursPerDay = CollectibleNet.DeserializeFloatVeryPrecise(p.HoursPerDay),
            DaysPerMonth = p.DaysPerMonth,
            CalendarSpeedMul = CollectibleNet.DeserializeFloatVeryPrecise(p.CalendarSpeedMul),
            // The same values can also be read from the client's calendar, with defaults
            // where it does not expose them; the flag is what tells the two apart.
            FromServer = true
        };

        string[]? names = p.TimeSpeedModifierNames;
        int[]? speeds = p.TimeSpeedModifierSpeeds;
        if (names == null || speeds == null) return settings;

        int count = p.TimeSpeedModifierNamesCount > 0 && p.TimeSpeedModifierNamesCount <= names.Length
            ? p.TimeSpeedModifierNamesCount
            : names.Length;

        for (int i = 0; i < count && i < speeds.Length; i++)
        {
            if (names[i] == null) continue;
            settings.TimeSpeedModifiers.Add(new CalendarSettings.TimeSpeedModifier(
                names[i], CollectibleNet.DeserializeFloatPrecise(speeds[i])));
        }
        return settings;
    }

    public static LevelInitPayload ToPayload(Packet_ServerLevelInitialize p) => new()
    {
        ServerChunkSize = p.ServerChunkSize,
        ServerMapChunkSize = p.ServerMapChunkSize,
        ServerMapRegionSize = p.ServerMapRegionSize,
        MaxViewDistance = p.MaxViewDistance
    };

    public static WorldMetaPayload ToPayload(Packet_WorldMetaData p) => new()
    {
        SeaLevel = p.SeaLevel,
        SunBrightness = p.SunBrightness,
        BlockLightLevels = TakeInts(p.BlockLightlevels, p.BlockLightlevelsCount),
        SunLightLevels = TakeInts(p.SunLightlevels, p.SunLightlevelsCount),
        // The same bytes the game puts into SaveGame.WorldConfigBytes — the local
        // save is assembled from them without a template world.
        WorldConfiguration = p.WorldConfiguration
    };

    /// <summary>From the assets packet we only take the block registry — the rest is unused.</summary>
    public static BlockRegistryPayload ToPayload(Packet_ServerAssets p)
    {
        var payload = new BlockRegistryPayload();
        var blocks = p.Blocks;
        if (blocks == null) return payload;

        int count = p.BlocksCount > 0 ? p.BlocksCount : blocks.Length;
        for (int i = 0; i < count && i < blocks.Length; i++)
        {
            var block = blocks[i];
            if (block?.Code == null) continue;
            payload.Blocks.Add(new BlockRegistryPayload.Entry(block.BlockId, block.Code));
        }
        return payload;
    }

    public static MapChunkPayload ToPayload(Packet_ServerMapChunk p) => new()
    {
        ChunkX = p.ChunkX,
        ChunkZ = p.ChunkZ,
        Ymax = p.Ymax,
        RainHeightMap = p.RainHeightMap,
        TerrainHeightMap = p.TerrainHeightMap
    };

    /// <summary>
    /// Arrays in Cito packets come as an "array + count" pair, and the count may
    /// be zero even when the array is filled. We trim to the exact length.
    /// </summary>
    private static int[] TakeInts(int[] values, int count)
    {
        if (values == null) return [];
        int n = count > 0 && count <= values.Length ? count : values.Length;
        if (n == values.Length) return values;
        var result = new int[n];
        Array.Copy(values, result, n);
        return result;
    }
}
