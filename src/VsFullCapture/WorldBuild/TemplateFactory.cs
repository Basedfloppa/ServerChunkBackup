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
    public static void CreateNew(string path, CaptureModel model, string worldName, bool writeBlockIds, Action<string> log)
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

        SaveGame save = BuildSaveGame(model, worldName, writeBlockIds, log);
        db.StoreGameData(SerializerUtil.Serialize(save));

        log($"Created a new world without a template: MapSize {save.MapSizeX}x{save.MapSizeY}x{save.MapSizeZ}, "
            + $"seed {save.Seed}, identifier {save.SavegameIdentifier}");
    }

    /// <summary>Assemble a SaveGame from the capture.</summary>
    public static SaveGame BuildSaveGame(CaptureModel model, string worldName, bool writeBlockIds, Action<string> log)
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
        if (writeBlockIds) ApplyBlockIds(save, model, log);
        ApplySpawn(save, model, log);
        BuildWorldConfigBytes(save, log);

        return save;
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
    /// Block registry: the game reads it on AssetsFirstLoaded and moves live blocks
    /// to the saved ids. Without it, with a foreign mod set the blocks would silently
    /// become the wrong ones.
    /// </summary>
    private static void ApplyBlockIds(SaveGame save, CaptureModel model, Action<string> log)
    {
        var registry = model.BlockRegistry();
        if (registry.Count == 0)
        {
            log("WARNING: no block registry in the capture — BlockIDs not written.");
            return;
        }

        save.ModData["BlockIDs"] = SerializerUtil.Serialize(registry);
        log($"BlockIDs: {registry.Count} id → code mappings");
    }

    /// <summary>Spawn point — the center of the captured area, so the player appears near the builds.</summary>
    private static void ApplySpawn(SaveGame save, CaptureModel model, Action<string> log)
    {
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
