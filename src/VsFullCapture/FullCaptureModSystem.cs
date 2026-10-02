using System.IO.Compression;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Maximum server data capture mod.
///
/// Works via the Harmony patch <c>SystemNetworkProcess.ProcessInBackground</c>
/// (see <see cref="PacketCapturePatch"/>) and writes a lossless capture file
/// (<see cref="CaptureFormat"/>) from which the offline tool assembles a real
/// <c>.vcdbs</c>.
///
/// Capture runs on the client only: on the server there is nothing to save —
/// it already owns this data.
/// </summary>
public class FullCaptureModSystem : ModSystem
{
    public const string ModId = "vsfullcapture";
    public const string ConfigFileName = ModId + ".json";
    public const string HarmonyId = "vschunkdump.fullcapture";

    /// <summary>Settings accessed by the Harmony patch.</summary>
    internal static FullCaptureConfig ActiveConfig { get; private set; } = new();

    private ICoreClientAPI? _capi;
    private CaptureWriter? _writer;
    private AutoWorldBuilder? _autoBuilder;
    private Harmony? _harmony;
    private long _tickListenerId = -1;
    private long _lastFlushMs;
    private long _lastManifestMs;
    private string _captureRoot = "";
    private volatile bool _shuttingDown;
    private bool _worldInfoFallbackDone;
    private bool _localRegistrySnapshotDone;

    public override double ExecuteOrder() => 1.0;

    /// <summary>
    /// The patch is installed as early as possible. The client starts connecting
    /// to the server before StartClientSide runs (mods and assets load first),
    /// and the identification packet can slip past the patch. In Start we make it
    /// before the connection.
    /// </summary>
    public override void Start(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Client) return;
        InstallPatch();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;

        ActiveConfig = api.LoadModConfig<FullCaptureConfig>(ConfigFileName) ?? new FullCaptureConfig();
        api.StoreModConfig(ActiveConfig, ConfigFileName);

        if (!ActiveConfig.Enabled)
        {
            Mod.Logger.Notification("Capture disabled in {0}", ConfigFileName);
            return;
        }

        try
        {
            _captureRoot = ResolveCaptureRoot(api);
            var manifest = new CaptureManifest
            {
                GameVersion = GameVersion.ShortGameVersion,
                ModVersion = Mod.Info.Version
            };
            _writer = new CaptureWriter(_captureRoot, manifest,
                CapturePipeline.CompressionLevelFor(ActiveConfig.CompressionLevel),
                m => Mod.Logger.Warning("{0}", m));
        }
        catch (Exception e)
        {
            Mod.Logger.Error("Failed to open capture in \"{0}\": {1}", _captureRoot, e);
            _writer = null;
            return;
        }

        CapturePipeline.Start(_writer, ActiveConfig, Mod.Logger);
        InstallPatch();

        // Entities are captured from the live objects (the patch sees nothing about them),
        // so this is subscribed here and not in the packet patch.
        if (ActiveConfig.CaptureEntities) EntityCapture.Install(api, Mod.Logger);

        // The world state is read from the client API as well (the clock and the player),
        // so it is installed unconditionally; the settings are checked where it is used.
        WorldStateCapture.Install(api, Mod.Logger);

        _autoBuilder = new AutoWorldBuilder(ActiveConfig, Mod.Logger, _captureRoot, api);
        api.Event.LeftWorld += OnLeftWorld;

        _tickListenerId = api.Event.RegisterGameTickListener(OnTick, 1000);
        RegisterCommands(api);

        Mod.Logger.Notification("v{0}: capturing server data to {1}", Mod.Info.Version, _captureRoot);
    }

    /// <summary>
    /// The patch is installed once: in singleplayer ModSystem.Start is called
    /// for both the client and the server, and Harmony stores patches globally.
    /// </summary>
    private void InstallPatch()
    {
        if (Harmony.HasAnyPatches(HarmonyId)) return;

        try
        {
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(FullCaptureModSystem).Assembly);
            Mod.Logger.Notification("Harmony patch ProcessInBackground installed");
        }
        catch (Exception e)
        {
            // After a game update the method signature may have changed. In that
            // case the patch is not applied, but the client must not crash.
            _harmony = null;
            Mod.Logger.Error("Harmony patch not installed: {0}", e);
        }
    }

    private void OnTick(float dt)
    {
        if (_shuttingDown || _writer == null || _capi == null) return;

        // The local registry snapshot is taken in singleplayer only: in
        // multiplayer the client holds the server's registry, and there is
        // nothing to translate with it. That is why this is BEFORE the capture
        // pause check.
        TrySnapshotLocalRegistry();

        // In singleplayer capture is not needed: the server is local, there is nothing to download.
        CapturePipeline.Paused = ActiveConfig.OnlyOnMultiplayer && _capi.IsSinglePlayer;

        if (CapturePipeline.Paused) return;

        EnsureWorldInfo();

        long now = _capi.World.ElapsedMilliseconds;
        EntityCapture.Tick(now);
        WorldStateCapture.Tick(now);

        if (now - _lastFlushMs >= ActiveConfig.FlushIntervalMs)
        {
            _lastFlushMs = now;
            CapturePipeline.Flush();
        }
        if (now - _lastManifestMs >= ActiveConfig.ManifestIntervalMs)
        {
            _lastManifestMs = now;
            CapturePipeline.SaveManifest();
        }
    }

    /// <summary>
    /// Take the local block registry once per session. While the registry is not
    /// fully ready (the world is still loading) the attempts are repeated — but
    /// no more than once per second, so only the outcome is written to the log.
    /// </summary>
    private void TrySnapshotLocalRegistry()
    {
        if (!ActiveConfig.SnapshotLocalRegistry || _localRegistrySnapshotDone) return;
        if (_capi == null || !_capi.IsSinglePlayer) return;

        string result = LocalRegistryCache.TrySnapshot(_capi, ActiveConfig.VerboseLogging);
        if (!result.StartsWith("local registry snapshot", StringComparison.Ordinal))
        {
            if (ActiveConfig.VerboseLogging)
                Mod.Logger.VerboseDebug("Local registry: {0}", result);
            return;
        }

        _localRegistrySnapshotDone = true;
        Mod.Logger.Notification("{0}", result);
    }

    /// <summary>
    /// Fallback path for world parameters. The identification packet arrives once
    /// on connect and could have been missed (for example, if the connection
    /// started before the patch was installed). Without it the builder does not
    /// know the world height and seed, so we take them from the client API at the
    /// first opportunity.
    /// </summary>
    private void EnsureWorldInfo()
    {
        if (_worldInfoFallbackDone || CapturePipeline.ServerInfoSeen) return;

        var world = _capi!.World;
        var mapSize = world.BlockAccessor.MapSize;
        if (mapSize.Y <= 0) return; // world is not loaded yet

        var payload = new WorldInfoPayload
        {
            GameVersion = GameVersion.ShortGameVersion,
            SavegameIdentifier = world.SavegameIdentifier,
            MapSizeX = mapSize.X,
            MapSizeY = mapSize.Y,
            MapSizeZ = mapSize.Z,
            Seed = world.Seed
        };

        CapturePipeline.Write(CaptureRecordType.ServerIdentification, CapturePayloadCodec.Encode(payload));
        _worldInfoFallbackDone = true;

        Mod.Logger.Notification(
            "Identification packet not caught — world parameters taken from the client API: "
            + "MapSize {0}x{1}x{2}, seed {3}",
            mapSize.X, mapSize.Y, mapSize.Z, world.Seed);
    }

    private string ResolveCaptureRoot(ICoreClientAPI api)
    {
        string baseDir = api.GetOrCreateDataPath("FullCapture");
        string name = ActiveConfig.CaptureName ?? "";
        if (string.IsNullOrWhiteSpace(name)) name = api.World.SavegameIdentifier ?? "";
        if (string.IsNullOrWhiteSpace(name)) name = "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(baseDir, Sanitize(name));
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        string result = sb.ToString().Trim('.', '_');
        return result.Length > 0 ? result : "capture";
    }

    private void RegisterCommands(ICoreClientAPI api)
    {
        api.ChatCommands.Create("fullcapture")
            .WithDescription("Capture all data sent by the server (VS Full Capture)")
            .BeginSubCommand("status")
                .WithDescription("How many records are captured and what is queued")
                .HandleWith(_ => TextCommandResult.Success(CapturePipeline.StatusText()))
            .EndSubCommand()
            .BeginSubCommand("flush")
                .WithDescription("Flush capture buffers to disk")
                .HandleWith(_ =>
                {
                    CapturePipeline.Flush();
                    CapturePipeline.SaveManifest();
                    return TextCommandResult.Success("Flushed. " + CapturePipeline.StatusText());
                })
            .EndSubCommand()
            .BeginSubCommand("where")
                .WithDescription("Show the capture directory")
                .HandleWith(_ => TextCommandResult.Success(_captureRoot))
            .EndSubCommand()
            .BeginSubCommand("registry")
                .WithDescription("Show or refresh the local block registry snapshot")
                .WithAdditionalInformation(
                    "The registry is needed to translate block ids directly in the data when building the world. "
                    + "It is only taken in singleplayer, and only for a world with dense block "
                    + "numbering (a world created here, not one opened with foreign ids).")
                .HandleWith(_ =>
                {
                    string result = _capi == null
                        ? "Client API unavailable."
                        : LocalRegistryCache.TrySnapshot(_capi, verbose: true);
                    return TextCommandResult.Success(result);
                })
            .EndSubCommand()
            .BeginSubCommand("build")
                .WithDescription("Build a local world from the capture")
                .WithAdditionalInformation(
                    "Creates <VintagestoryData>/Saves/<name>.vcdbs from the captured data. "
                    + "No template world is needed: world parameters and configuration are taken from the capture. "
                    + "The build runs in the background, progress is written to the log. "
                    + "The finished world will appear in the singleplayer world list.")
                .WithExamples(".fullcapture build", ".fullcapture build My server")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("name"))
                .HandleWith(OnBuildCommand)
            .EndSubCommand();
    }

    private TextCommandResult OnBuildCommand(TextCommandCallingArgs args)
    {
        if (_autoBuilder == null)
            return TextCommandResult.Error("Capture is not initialized — is it enabled in the settings?");

        // Mark the moment the build happens: the periodic snapshot may be half a minute old,
        // and it is this moment that should decide the time and the spawn point of the world.
        WorldStateCapture.CaptureNow();

        // Flush and drain the buffers so that everything written is in the file the build reads.
        CapturePipeline.Flush();
        CapturePipeline.WaitForQueue();
        CapturePipeline.SaveManifest();

        string? name = args.Parsers.Count > 0 ? args[0] as string : null;
        return TextCommandResult.Success(_autoBuilder.StartBuild(name));
    }

    private void OnLeftWorld()
    {
        if (_shuttingDown) return;

        // The next singleplayer world may have a different mod set — we take the
        // local registry snapshot again (in multiplayer it is not taken).
        if (_capi?.IsSinglePlayer == true) _localRegistrySnapshotDone = false;

        if (ActiveConfig.OnlyOnMultiplayer && _capi != null && _capi.IsSinglePlayer)
        {
            CapturePipeline.Paused = true;
            return;
        }

        // The state of the world just left: the player is still there, but reading the API
        // while the world is torn down is not safe, so the snapshot from the last tick goes in.
        WorldStateCapture.WriteLast();

        // Let the writer thread finish the tail of the queue, so that everything captured —
        // including the state just written — is in the file the build is about to read.
        CapturePipeline.Flush();
        CapturePipeline.WaitForQueue();
        CapturePipeline.SaveManifest();

        // The next server will get its own block registry: it arrives once per
        // connection, and there may be several servers in one game run.
        CapturePipeline.ClearOneShots();

        // World parameters are also reset for the next connection:
        // if the identification packet is not caught, the fallback must work again.
        CapturePipeline.ResetServerInfo();
        _worldInfoFallbackDone = false;

        // Entity ids belong to the world that has just been left: the next server
        // numbers its entities from scratch.
        EntityCapture.ResetSession();

        // The calendar belongs to that world as well: its clock would put the next
        // server's time into the previous world's calendar.
        WorldStateCapture.ResetSession();

        _autoBuilder?.OnLeftWorld();
    }

    public override void Dispose()
    {
        _shuttingDown = true;

        try
        {
            if (_capi != null)
            {
                _capi.Event.LeftWorld -= OnLeftWorld;
                if (_tickListenerId >= 0) _capi.Event.UnregisterGameTickListener(_tickListenerId);
            }
        }
        catch (Exception) { /* on exit the engine may already be partially torn down */ }

        try
        {
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
        }
        catch (Exception) { /* see above */ }

        EntityCapture.Uninstall();
        WorldStateCapture.Uninstall();
        CapturePipeline.Stop();

        try { _writer?.Dispose(); } catch (Exception) { /* see above */ }
        _writer = null;
    }
}
