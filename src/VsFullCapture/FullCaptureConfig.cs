namespace VsFullCapture;

/// <summary>
/// Capture settings. File: &lt;VintagestoryData&gt;/ModConfig/vsfullcapture.json
/// </summary>
public class FullCaptureConfig
{
    /// <summary>Enable capture.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Work only in multiplayer. In singleplayer the server is local:
    /// there is nothing to download, and the packet stream, disk writes and
    /// auto-build on leaving the world only burn memory and time.
    /// </summary>
    public bool OnlyOnMultiplayer { get; set; } = true;

    /// <summary>Capture directory name. If empty, the save identifier is used.</summary>
    public string? CaptureName { get; set; } = null;

    /// <summary>Write chunks (core data: blocks, light, liquids, decor, moddata).</summary>
    public bool CaptureChunks { get; set; } = true;

    /// <summary>Write column heightmaps (required, otherwise the world will not load our chunks).</summary>
    public bool CaptureMapChunks { get; set; } = true;

    /// <summary>Write the block registry and other assets (needed to map block ids).</summary>
    public bool CaptureServerAssets { get; set; } = true;

    /// <summary>
    /// Write assets only once per session. Assets weigh tens of megabytes
    /// and arrive again on reconnect.
    /// </summary>
    public bool ServerAssetsOncePerSession { get; set; } = true;

    /// <summary>Write queue depth. On overflow records are lost (with a counter).</summary>
    public int MaxQueueDepth { get; set; } = 1024;

    /// <summary>How often to flush the file buffer to disk, ms.</summary>
    public int FlushIntervalMs { get; set; } = 5000;

    /// <summary>How often to update capture.json, ms.</summary>
    public int ManifestIntervalMs { get; set; } = 30000;

    /// <summary>Brotli compression level: 0 = Fastest, 1 = Optimal, 2 = no compression.</summary>
    public int CompressionLevel { get; set; } = 0;

    /// <summary>
    /// Automatically build the local world when leaving the world or the server.
    /// </summary>
    public bool BuildOnLeftWorld { get; set; } = true;

    /// <summary>Name of the world to create. If empty, "Captured &lt;part of the identifier&gt;".</summary>
    public string? BuiltWorldName { get; set; } = null;

    /// <summary>Do not build the world if the capture has fewer than this many complete columns.</summary>
    public int MinColumnsToBuild { get; set; } = 16;

    /// <summary>Overwrite an already built world with the same name.</summary>
    public bool OverwriteBuiltWorld { get; set; } = true;

    /// <summary>Verbose log.</summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>
    /// Local block registry file ("id in this world → code") used to translate
    /// ids when building the world. If empty, the snapshot the mod takes itself
    /// is used: &lt;VintagestoryData&gt;/FullCapture/localregistry.json.
    ///
    /// Without this registry there is nothing to translate ids with, and the mod
    /// will write BlockIDs — then the game will be asked to remap the blocks.
    /// </summary>
    public string? LocalRegistryPath { get; set; } = null;

    /// <summary>
    /// Take a snapshot of the local block registry while the game runs in
    /// singleplayer. That is the only place where THIS world's registry is visible:
    /// in multiplayer the client receives the server's registry, and there is
    /// nothing to translate with it.
    ///
    /// The snapshot is only taken for "dense" numbering without gaps and without
    /// placeholder blocks: for a world that has already been opened with foreign
    /// ids, the numbering is mixed and is not suitable as a local one.
    /// </summary>
    public bool SnapshotLocalRegistry { get; set; } = true;

    /// <summary>
    /// Block code for blocks missing from this world. Empty — they become air.
    /// </summary>
    public string? MissingBlockCode { get; set; } = null;
}
