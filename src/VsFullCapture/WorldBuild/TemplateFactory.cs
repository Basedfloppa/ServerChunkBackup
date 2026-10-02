using System.Collections.Concurrent;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.Common.Database;
using Vintagestory.Server;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Creates a local save from scratch — without a template world.
///
/// The game finds worlds by simply scanning <c>&lt;VintagestoryData&gt;/Saves/*.vcdbs</c>
/// (GuiScreenSingleplayer), so it is enough to put a valid file there. The
/// <c>chunk</c> and <c>mapchunk</c> rows are appended by <see cref="WorldBuilder"/>,
/// and only <c>gamedata</c> is assembled here — the part the client otherwise
/// cannot get.
///
/// The key point: the world configuration bytes in the <c>WorldMetaData</c> packet
/// are exactly what the game writes into <c>SaveGame.WorldConfigBytes</c>
/// (ServerMain: <c>SetWorldConfiguration(WorldConfiguration.ToBytes())</c>).
/// So the world height, landscape, climate and server rules carry over as is.
/// </summary>
public static class TemplateFactory
{
    /// <summary>worldgen version used by 1.22.x worlds.</summary>
    private const int DefaultWorldGenVersion = 3;

    /// <summary>Chunk data format version (see ChunkData.CompressInto).</summary>
    private const int ChunkDataVersion = 2;

    /// <summary>Create a new world file and write gamedata into it.</summary>
    public static void CreateNew(string path, CaptureModel model, string worldName,
        BlockRegistryTable? blockIds, Action<string> log)
    {
        var logger = new SilentLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;

        string? error = null;
        if (!connection.OpenOrCreate(path, ref error, requireWriteAccess: true,
                corruptionProtection: false, doIntegrityCheck: false))
        {
            throw new InvalidOperationException("Failed to create the world" + (error != null ? ": " + error : ""));
        }

        // In case the database was created for the first time: guarantee the tables.
        db.UpgradeToWriteAccess();

        SaveGame save = BuildSaveGame(model, worldName, blockIds, log);
        db.StoreGameData(SerializerUtil.Serialize(save));

        log($"Created a new world without a template: MapSize {save.MapSizeX}x{save.MapSizeY}x{save.MapSizeZ}, "
            + $"seed {save.Seed}, identifier {save.SavegameIdentifier}");
    }

    /// <summary>Assemble a SaveGame from the capture.</summary>
    public static SaveGame BuildSaveGame(CaptureModel model, string worldName,
        BlockRegistryTable? blockIds, Action<string> log)
    {
        var info = model.Identification;

        var save = new SaveGame
        {
            MapSizeX = model.MapSizeX,
            MapSizeY = model.MapSizeY,
            MapSizeZ = model.MapSizeZ,
            Seed = info?.Seed ?? 0,
            WorldName = worldName,

            // The server's game style is not saved in the capture (the identification
            // packet arrives before mods load), so we use the regular survival one.
            PlayStyle = "surviveandbuild",
            PlayStyleLangCode = "surviveandbuild-bands",
            WorldType = "standard",

            // Our own identifier: the client's map cache is keyed by it, and the
            // server's foreign identifier would cause confusion with already downloaded maps.
            SavegameIdentifier = Guid.NewGuid().ToString(),

            CreatedGameVersion = GameVersion.ShortGameVersion,
            LastSavedGameVersion = GameVersion.ShortGameVersion,
            HighestChunkdataVersion = ChunkDataVersion,
            CreatedWorldGenVersion = DefaultWorldGenVersion,

            EntitySpawning = true,
            HoursPerDay = 24f,
            IsNewWorld = true,

            // The game sets these fields in its SaveGame.CreateNew. The values matter:
            // LastBlockItemMappingVersion < 1 makes the game re-apply the 1.12
            // legacy planter remaps (the log shows "Failed remaps entry
            // (clayplanter-... not found)"). The field is marked [Obsolete], but the
            // game still writes and reads it, so we set it the way the game does.
#pragma warning disable CS0618, CS0612
            LastBlockItemMappingVersion = GameVersion.BlockItemMappingVersion,
#pragma warning restore CS0618, CS0612
            CalendarSpeedMul = 0.5f,
            TimeSpeedModifiers = new Dictionary<string, float> { { "baseline", 60f } },
            LastHerdId = 1L,

            LandClaims = new List<LandClaim>(),
            RemappingsAppliedByCode = new Dictionary<string, bool>
            {
                // The game sets this flag when LastBlockItemMappingVersion >= 1;
                // duplicate it explicitly so the 1.12 planter remap definitely does not run.
                ["game:v1.12clayplanters"] = true
            },
            ModData = new ConcurrentDictionary<string, byte[]>(4, 16)
        };

        ApplyWorldConfiguration(save, model, log);
        if (blockIds is { Count: > 0 }) ApplyBlockIds(save, blockIds, log);
        ApplyEntityIdCounter(save, model, log);
        ApplyWorldState(save, model, log);
        ApplySpawn(save, model, log);
        BuildWorldConfigBytes(save, log);

        return save;
    }

    /// <summary>
    /// Continue the entity id counter after the captured entities.
    ///
    /// The game does not restore this counter when it loads entities from chunk rows
    /// (ServerMain.LoadEntity raises SaveGame.LastEntityId in repair mode only), so with
    /// a counter of 0 every newly spawned entity would hit an id a captured one already
    /// holds: ServerMain.SpawnEntity logs a warning and renumbers it. Raising the counter
    /// in advance keeps the log clean and the captured ids intact.
    /// </summary>
    private static void ApplyEntityIdCounter(SaveGame save, CaptureModel model, Action<string> log)
    {
        long maxId = model.HighestEntityId;
        if (maxId == 0) return;

        save.LastEntityId = Math.Max(save.LastEntityId, maxId);
        log($"Entity id counter: starts after {save.LastEntityId} (the captured entities keep their ids)");
    }

    /// <summary>
    /// Carry the world configuration over from the capture. It is what sets the
    /// world height and generation parameters, so without it the new world would
    /// be "the wrong one".
    /// </summary>
    private static void ApplyWorldConfiguration(SaveGame save, CaptureModel model, Action<string> log)
    {
        byte[]? configBytes = model.WorldMetaData?.WorldConfiguration;
        if (configBytes is not { Length: > 0 })
        {
            log("WARNING: no world configuration in the capture — the world was created with default settings "
                + "(the world height may not match the server).");
            return;
        }

        try
        {
            save.WorldConfiguration = TreeAttribute.CreateFromBytes(configBytes);
            log($"World configuration taken from the capture ({configBytes.Length} bytes)");
        }
        catch (Exception e)
        {
            log("WARNING: failed to parse the world configuration from the capture: " + e.Message);
        }
    }

    /// <summary>
    /// Block registry: the game reads it on AssetsFirstLoaded and moves live blocks to
    /// the saved ids. Without it the game has no way to learn what the ids in the world
    /// mean and reads every chunk with its own numbering.
    ///
    /// Which registry it must be is decided by <see cref="WorldBuilder"/>: the target of
    /// the translation when the ids are translated, the server's otherwise.
    /// </summary>
    private static void ApplyBlockIds(SaveGame save, BlockRegistryTable registry, Action<string> log)
    {
        save.ModData["BlockIDs"] = SerializerUtil.Serialize(registry.Blocks);
        log($"BlockIDs: {registry.Count} id → code mappings");
    }

    /// <summary>
    /// The clock the world starts at. The time of day, the day of the year and the season
    /// are all derived by the game from this one number, so writing it is what saves the
    /// user from <c>/time set</c> — and the calendar settings travel with it, because a
    /// world with a different day length would turn the same clock into another date.
    /// </summary>
    internal static void ApplyWorldState(SaveGame save, CaptureModel model, Action<string> log)
    {
        var state = model.WorldState;
        if (state == null)
        {
            log("WARNING: the capture has no world state — the world will start at its own "
                + "midnight, in its first spring. The capture was taken by a build without its "
                + "support, or with CaptureWorldState disabled.");
            return;
        }

        var calendar = state.Calendar;

        // A negative clock is undefined for the game: it clamps it back to 0 with a warning.
        long total = Math.Max(0, state.TotalSeconds);
        save.TotalGameSeconds = total;

        // The world age is the clock minus its start, so a start after the clock would make
        // it negative. The clock never runs backwards, but a broken capture can.
        long start = Math.Clamp(calendar.TotalSecondsStart, 0, total);
        save.TotalGameSecondsStart = start;

        // Zero hours per day makes the game throw on load, and a zero time speed would freeze
        // the world: on either, the save's own default is kept.
        if (calendar.HoursPerDay > 0) save.HoursPerDay = calendar.HoursPerDay;
        if (calendar.CalendarSpeedMul > 0) save.CalendarSpeedMul = calendar.CalendarSpeedMul;
        if (calendar.TimeSpeedModifiers.Count > 0)
        {
            // Grouped, not ToDictionary: a name written twice would throw, and the last
            // value is the one the game would end up with anyway.
            save.TimeSpeedModifiers = calendar.TimeSpeedModifiers
                .GroupBy(m => m.Name)
                .ToDictionary(g => g.Key, g => g.Last().Speed);
        }

        log($"Time: {state.ClientDate ?? total + "s"}"
            + (state.Season != null ? $", {state.Season}" : "")
            + $" (clock {total}s, world age {(total - start) / 86400.0:F1} days, "
            + $"{calendar.HoursPerDay:0.##}h day, {calendar.DaysPerMonth}-day months, "
            + (calendar.FromServer ? "from the server calendar)" : "from the client calendar)"));
    }

    /// <summary>
    /// Spawn point — where the player stood when the capture ended, so the assembled world
    /// opens at the captured place instead of asking for a <c>/tp</c>. Older captures have
    /// no position, and a position that does not fit the built world is refused: for those
    /// the centre of the captured area is used, as before.
    /// </summary>
    internal static void ApplySpawn(SaveGame save, CaptureModel model, Action<string> log)
    {
        var state = model.WorldState;
        if (state is { HasPlayer: true } && TrySpawnAtPlayer(save, state, log)) return;

        if (!TryGetCenter(model, out int centerX, out int centerZ)) return;

        save.DefaultSpawn = new PlayerSpawnPos
        {
            x = centerX,
            y = null, // the game will pick the height from the terrain itself
            z = centerZ
        };
        log($"Spawn point: {centerX}, {centerZ} (center of the captured area)");
    }

    /// <summary>
    /// The captured player position as the world's spawn point. Returns false if it cannot
    /// be used, and then says why.
    /// </summary>
    private static bool TrySpawnAtPlayer(SaveGame save, WorldStatePayload state, Action<string> log)
    {
        // A position that is not a number would become 0, 0, 0 in the casts below and quietly
        // put the player in a corner of the map. The capture refuses to write such a position,
        // but a hand-made record can hold one.
        if (!double.IsFinite(state.PlayerX) || !double.IsFinite(state.PlayerY) || !double.IsFinite(state.PlayerZ))
        {
            log("WARNING: the captured player position is not a number — the spawn point is the "
                + "centre of the captured area instead.");
            return false;
        }

        int x = (int)Math.Floor(state.PlayerX);
        int y = (int)Math.Floor(state.PlayerY);
        int z = (int)Math.Floor(state.PlayerZ);

        // A spawn outside the map makes the game throw while loading the world
        // (ServerMain.EntityPosFromSpawnPos), so it is refused here, where it costs a line
        // in the log instead of an unloadable world.
        if (x < 0 || x >= save.MapSizeX || z < 0 || z >= save.MapSizeZ || y < 0 || y >= save.MapSizeY)
        {
            log($"WARNING: the captured player position {x}, {y}, {z} is outside the world "
                + $"({save.MapSizeX}x{save.MapSizeY}x{save.MapSizeZ}) — the spawn point is the centre "
                + "of the captured area instead.");
            return false;
        }

        save.DefaultSpawn = new PlayerSpawnPos
        {
            x = x,
            // The height is kept rather than left to the terrain: the captured position is a
            // place, not a column, and a player who was in a cave or inside a building would
            // otherwise be put on the surface above it.
            y = y,
            z = z,
            // The facing is honoured. The pitch is not stored at all: the game overwrites it
            // with its own value for every spawn point, so recording it would be a lie.
            yaw = state.PlayerYaw
        };
        log($"Spawn point: {x}, {y}, {z} — where the player stood (yaw {state.PlayerYaw:F2})");
        return true;
    }

    /// <summary>
    /// Build WorldConfigBytes from WorldConfiguration. The WillSave method is internal,
    /// but it does exactly this (plus setting LastPlayed), so we call it via
    /// reflection instead of duplicating the logic.
    /// </summary>
    private static void BuildWorldConfigBytes(SaveGame save, Action<string> log)
    {
        var willSave = typeof(SaveGame).GetMethod("WillSave", BindingFlags.NonPublic | BindingFlags.Instance);
        if (willSave == null)
        {
            log("WARNING: SaveGame.WillSave not found — the world configuration may not make it into the save.");
            return;
        }

        try
        {
            using var ms = new FastMemoryStream();
            willSave.Invoke(save, [ms]);
        }
        catch (Exception e)
        {
            log("WARNING: failed to build the world configuration for the save: " + e.Message);
        }
    }

    /// <summary>Center of the captured area in blocks.</summary>
    private static bool TryGetCenter(CaptureModel model, out int centerX, out int centerZ)
    {
        centerX = centerZ = 0;
        if (model.Chunks.Count == 0) return false;

        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (var key in model.Chunks.Keys)
        {
            if (key.X < minX) minX = key.X;
            if (key.X > maxX) maxX = key.X;
            if (key.Z < minZ) minZ = key.Z;
            if (key.Z > maxZ) maxZ = key.Z;
        }

        centerX = (minX + maxX) / 2 * 32 + 16;
        centerZ = (minZ + maxZ) / 2 * 32 + 16;
        return true;
    }
}
