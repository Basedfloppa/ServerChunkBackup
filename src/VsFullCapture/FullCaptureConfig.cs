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

    /// <summary>
    /// Write entities (mobs, dropped items, item frames) into the chunks.
    /// Entities live inside a chunk row, so this only makes sense together with
    /// <see cref="CaptureChunks"/>.
    /// </summary>
    public bool CaptureEntities { get; set; } = true;

    /// <summary>
    /// How often to re-write the state of the loaded entities, ms (0 — only at spawn).
    ///
    /// Needed because positions come over UDP (<c>Packet_UdpPacket.BulkPositions</c>),
    /// which the capture does not see: without the refresh the entities would stand
    /// where they spawned. Each pass writes every loaded entity again, so a large
    /// world adds about <c>entities × record size</c> to the capture file per pass.
    /// </summary>
    public int CaptureEntityRefreshIntervalMs { get; set; } = 30000;

    /// <summary>
    /// How many entities one refresh pass re-serializes per game tick. Serialization
    /// goes through the live objects, so it happens on the main thread; the pass is
    /// spread over several ticks so that a world with hundreds of entities does not
    /// cause a freeze.
    /// </summary>
    public int CaptureEntitiesPerTick { get; set; } = 64;

    /// <summary>
    /// Capture the world state: the game clock (from which both the time of day and the
    /// season follow) and the player position. The builder writes them into the save as
    /// <c>TotalGameSeconds</c> and <c>DefaultSpawn</c>, so the assembled world opens at
    /// the captured moment with the player already where the capture was taken instead
    /// of at its own midnight, needing <c>/time set</c> and <c>/tp</c>.
    /// </summary>
    public bool CaptureWorldState { get; set; } = true;

    /// <summary>
    /// How often to write the world state to the capture, ms.
    ///
    /// The state is read every tick and its last value is written when leaving the world
    /// or starting a build, so this only bounds how much is lost if the game dies
    /// mid-session. The record is a hundred bytes.
    /// </summary>
    public int WorldStateIntervalMs { get; set; } = 30000;

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

    /// <summary>
    /// An optional extra bound on top of the chunk rule: leave out entities that no record
    /// mentioned for more than this many connections (game sessions). Non-positive (the
    /// default) applies no such bound.
    ///
    /// Entity freshness itself is decided by the chunk rule, which is always applied: an
    /// entity is left out when the chunk it stands in was received again in a later session
    /// that recorded entities, and the entity was not among them. The capture is appended to
    /// across game runs, so a mob that died out of view never produces a removal record —
    /// but a place the client never returned to says nothing about its mobs, and nothing
    /// there is erased.
    ///
    /// A positive value cuts the leftovers by age instead: entities whose chunk was never
    /// received again. The build log always reports how many were left out and why.
    /// </summary>
    public int EntityMaxAgeConnections { get; set; } = -1;

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
