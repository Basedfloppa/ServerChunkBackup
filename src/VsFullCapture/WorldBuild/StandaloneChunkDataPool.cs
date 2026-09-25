using Vintagestory.Common;

namespace VsFullCapture;

/// <summary>
/// A stub chunk data pool. <see cref="ChunkDataPool"/> has a protected constructor,
/// so a full-fledged ServerChunk can be assembled without a ServerMain instance
/// (that is, offline, without starting the game).
/// </summary>
public sealed class StandaloneChunkDataPool : ChunkDataPool
{
    public StandaloneChunkDataPool(int chunkSize = 32)
    {
        chunksize = chunkSize;
        BlackHoleData = ChunkData.CreateNew(chunkSize, this);
        OnlyAirBlocksData = NoChunkData.CreateNew(chunkSize);
    }

    /// <summary>
    /// The base ChunkDataPool checks server.RunPhase here, and we have no server
    /// (that is the whole point of the stub). Without this override block writes
    /// fail: ChunkDataLayer.Set calls this property.
    /// </summary>
    public override bool ShuttingDown => false;
}
