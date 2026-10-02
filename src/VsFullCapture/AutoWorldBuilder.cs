using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Builds a local world from the capture while the game is running: creates a
/// file at <c>&lt;VintagestoryData&gt;/Saves/&lt;name&gt;.vcdbs</c> that then
/// shows up in singleplayer. No template world is needed — <see cref="TemplateFactory"/>
/// assembles gamedata from the captured parameters, including the world configuration.
///
/// Everything is done on a background thread: the build reads tens of megabytes
/// and writes megabytes, which would freeze the game on the main thread.
/// </summary>
public sealed class AutoWorldBuilder
{
    private readonly FullCaptureConfig _config;
    private readonly ILogger _logger;
    private readonly string _captureRoot;
    private readonly ICoreClientAPI _capi;
    private int _running;

    /// <summary>Notes gathered while reading the capture, appended to build.log.</summary>
    private readonly StringBuilder _details = new();

    public AutoWorldBuilder(FullCaptureConfig config, ILogger logger, string captureRoot, ICoreClientAPI capi)
    {
        _config = config;
        _logger = logger;
        _captureRoot = captureRoot;
        _capi = capi;
    }

    /// <summary>Build on the "left world/server" event.</summary>
    public void OnLeftWorld()
    {
        if (!_config.BuildOnLeftWorld) return;

        // In singleplayer there is nothing to capture — and nothing to build.
        if (_config.OnlyOnMultiplayer && _capi.IsSinglePlayer)
        {
            _logger.Notification("Singleplayer — build not started.", FullCaptureModSystem.ModId);
            return;
        }

        // LeftWorld also fires when the client initializes (leaving an "empty"
        // session before the main menu), so we build only if something was
        // actually captured in this session. Otherwise the game would rebuild the
        // old capture on every launch.
        if (CapturePipeline.Paused || CapturePipeline.Written <= 0)
        {
            return;
        }

        StartBuild(null);
    }

    /// <summary>Start the build in the background. Returns text for chat.</summary>
    public string StartBuild(string? worldName)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return "A build is already running, wait for it to finish (progress in the log).";
        }

        string captureRoot = _captureRoot;
        if (!File.Exists(Path.Combine(captureRoot, CaptureFormat.FileName)))
        {
            Volatile.Write(ref _running, 0);
            return "Capture is empty: file " + CaptureFormat.FileName + " not found in " + captureRoot;
        }

        var thread = new Thread(() => Run(captureRoot, worldName))
        {
            IsBackground = true,
            Name = "vsfullcapture-build"
        };
        thread.Start();

        return "World build started in the background. Progress is in the log, the result will appear in the singleplayer world list.";
    }

    private void Run(string captureRoot, string? worldName)
    {
        try
        {
            _logger.Notification("Reading capture: {1}", FullCaptureModSystem.ModId, captureRoot);
            var model = CaptureModel.Load(captureRoot, m =>
            {
                _logger.Warning("{1}", FullCaptureModSystem.ModId, m);
                _details.AppendLine("  " + m.Trim());
            });

            if (model.DamagedRegions > 0)
            {
                string damage = $"Capture is damaged: {model.DamagedRegions} region(s), "
                                + $"{model.DamagedBytes / 1024.0:F1} KiB lost. Everything after the damage was read; "
                                + "the world may miss the chunks and entities in those few records.";
                _logger.Warning("{1}", FullCaptureModSystem.ModId, damage);
                _details.AppendLine("  " + damage);
            }

            if (model.TruncatedTail)
            {
                string tail = "The capture ends with an incomplete record — the last moment before the game "
                              + "was terminated is missing.";
                _logger.Warning("{1}", FullCaptureModSystem.ModId, tail);
                _details.AppendLine("  " + tail);
            }

            var (complete, noMapChunk, incomplete) = model.ColumnReadiness();
            _logger.Notification(
                "Columns in capture: ready {1}, no heightmap {2}, incomplete {3}",
                FullCaptureModSystem.ModId, complete, noMapChunk, incomplete);

            if (complete < _config.MinColumnsToBuild)
            {
                Finish(captureRoot, "SKIP",
                    $"Not built: only {complete} complete columns in the capture, {_config.MinColumnsToBuild} required.");
                _logger.Warning(
                    "Too little data ({1} complete columns < {2}) — not building the world.",
                    FullCaptureModSystem.ModId, complete, _config.MinColumnsToBuild);
                return;
            }

            string name = ResolveWorldName(worldName, captureRoot);
            string path = Path.Combine(GamePaths.Saves, Sanitize(name) + ".vcdbs");

            if (File.Exists(path) && !_config.OverwriteBuiltWorld)
            {
                Finish(captureRoot, "SKIP", $"Not built: \"{name}\" already exists and overwriting is disabled.");
                _logger.Warning("World \"{1}\" already exists and overwriting is disabled.", FullCaptureModSystem.ModId, name);
                return;
            }

            var localRegistry = ResolveLocalRegistry();
            var options = new BuildOptions
            {
                OutputPath = path,
                WorldName = name,
                LocalRegistry = localRegistry,
                MissingBlockCode = _config.MissingBlockCode,
                EntityMaxAgeConnections = _config.EntityMaxAgeConnections,
                // The world's existence and OverwriteBuiltWorld were already checked above.
                Force = true
                // WriteBlockIds stays enabled: the world always needs the id → code table,
                // and WorldBuilder picks the right one — the target registry when the ids
                // are translated, the server's otherwise.
            };

            var report = WorldBuilder.Build(model, options,
                m => _logger.Notification("{1}", FullCaptureModSystem.ModId, m));

            foreach (string warning in report.Warnings)
            {
                _logger.Warning("{1}", FullCaptureModSystem.ModId, warning);
            }

            if (report.BlockIdsTranslated)
            {
                _logger.Notification("{1}", FullCaptureModSystem.ModId,
                    report.IdTranslation ?? "block ids translated using the supplied map");
                _logger.Notification(
                    "Palettes: entries {0}, changed {1}, replaced with fallback block {2} "
                    + "(compressed layers {3}, raw {4}).",
                    report.PaletteEntries, report.PaletteChanged,
                    report.PaletteFallback, report.PaletteCompressedLayers, report.PaletteRawLayers);
            }

            _logger.Notification(
                "Block entities: {0} (rewritten {1}, skipped {2}, of them {3} not on their block); "
                + "chiseled materials translated to codes {4}",
                report.BlockEntities, report.BlockEntitiesRewritten,
                report.BlockEntitiesDropped,
                report.BlockEntitiesStaleBlock + report.BlockEntitiesWrongBlock,
                report.MaterialsAsCodes);

            if (report.BlockEntities == 0)
            {
                _logger.Warning(
                    "No block entities in the capture: chiseled blocks, chests and machines will stay "
                    + "empty. The capture was taken by a build without their support — a new capture from the server is needed.");
            }

            _logger.Notification(
                "Entities: {0} written of {1} in the capture (skipped {2}, superseded {3}, older than the bound {4}, "
                + "bound {5})",
                report.Entities, report.EntitiesInCapture, report.EntitiesDropped,
                report.EntitiesSuperseded, report.EntitiesTooOld,
                report.EntityMaxAgeUsed < 0 ? "none" : $"{report.EntityMaxAgeUsed} connection(s)");

            if (report.EntitiesInCapture == 0)
            {
                _logger.Warning(
                    "No entities in the capture: mobs, dropped items and item frames will be missing in the world. "
                    + "The capture was taken by a build without their support (or with CaptureEntities disabled) — "
                    + "a new capture from the server is needed.");
            }

            _logger.Notification(
                "World built: {0} columns, {1} chunks, {2} heightmaps → {3}",
                report.ColumnsWritten, report.ChunksWritten,
                report.MapChunksWritten, path);
            _logger.Notification("Open it in singleplayer: \"{0}\"", name);

            Finish(captureRoot, "OK",
                $"Built \"{name}\": {report.ColumnsWritten} columns, {report.ChunksWritten} chunks, "
                + $"{report.Entities} of {report.EntitiesInCapture} entities"
                + (report.EntitiesSuperseded > 0
                    ? $" ({report.EntitiesSuperseded} superseded by a later look at their chunk)"
                    : "")
                + $", {report.BlockEntities} block entities{(model.DamagedRegions > 0 ? $", {model.DamagedRegions} damaged region(s) skipped" : "")}. "
                + $"Open it in singleplayer.");
        }
        catch (Exception e)
        {
            _logger.Error("Failed to build the world: {1}", FullCaptureModSystem.ModId, e);
            Finish(captureRoot, "FAIL", "Build failed: " + e.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// Say how the build ended, in three places at once. The log is where the details are,
    /// but a build runs after the player has left the world — without a word in chat and a
    /// line on disk, a build that fails every time looks exactly like a build that works.
    /// </summary>
    private void Finish(string captureRoot, string status, string summary)
    {
        _logger.Notification("{0}", summary);

        try
        {
            File.AppendAllText(Path.Combine(captureRoot, "build.log"),
                $"{DateTime.Now:u}  {status,-4}  {summary}{Environment.NewLine}{_details}");
        }
        catch (Exception)
        {
            // A build that cannot write its own report must still report through chat and the log.
        }

        try
        {
            _capi.Event.EnqueueMainThreadTask(
                () => _capi.ShowChatMessage("[serverchunkbackup] " + summary),
                "vsfullcapture-build");
        }
        catch (Exception)
        {
            // The world may already be gone; the log and build.log still carry the outcome.
        }
    }

    /// <summary>
    /// Get the local block registry: from the settings or from the snapshot the mod
    /// takes in singleplayer. Returns null if there is no registry — then the build
    /// goes the old way through BlockIDs (and there will be a warning about it).
    /// </summary>
    private BlockRegistryTable? ResolveLocalRegistry()
    {
        try
        {
            var table = LocalRegistryCache.Load(_config, _capi,
                m => _logger.Notification("{1}", FullCaptureModSystem.ModId, m));

            if (table == null)
            {
                _logger.Warning(
                    "No local block registry — nothing to translate ids with, writing BlockIDs "
                    + "(the game will remap the blocks). To translate ids in the data, open "
                    + "any singleplayer world once: the mod will remember the registry in {1}.",
                    FullCaptureModSystem.ModId, LocalRegistryCache.SnapshotPath(_capi));
            }

            return table;
        }
        catch (Exception e)
        {
            _logger.Warning("Local registry not read ({1}) — not translating ids.",
                FullCaptureModSystem.ModId, e.Message);
            return null;
        }
    }

    private string ResolveWorldName(string? worldName, string captureRoot)
    {
        if (!string.IsNullOrWhiteSpace(worldName)) return worldName!;
        if (!string.IsNullOrWhiteSpace(_config.BuiltWorldName)) return _config.BuiltWorldName!;

        // By default, after the capture directory name (this is the server world identifier).
        string folder = Path.GetFileName(captureRoot.TrimEnd(Path.DirectorySeparatorChar, '/'));
        string shortId = folder.Length > 8 ? folder[..8] : folder;
        return string.IsNullOrWhiteSpace(shortId) ? "Captured world" : "Captured " + shortId;
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '_');
        }
        string result = sb.ToString().Trim('.', ' ');
        return result.Length > 0 ? result : "Captured world";
    }
}
