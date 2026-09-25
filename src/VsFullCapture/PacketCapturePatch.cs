using HarmonyLib;
using Vintagestory.Client.NoObf;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// The single interception point: <c>SystemNetworkProcess.ProcessInBackground(Packet_Server)</c>.
///
/// EVERY packet from the server passes through it — already parsed into a typed
/// <c>Packet_Server</c>, so all the needed data is available right here.
///
/// The patch must be cheap: the method runs on the background network thread
/// (<c>OnSeperateThreadGameTick</c>), and anything heavy here slows down packet
/// reception. That is why the prefix only queues a job, while field parsing,
/// encoding and writing happen on a separate thread (see <see cref="CapturePipeline"/>).
///
/// The method is private, so the name in the attribute is a string literal
/// (<c>nameof</c> for a private method from another class does not compile).
///
/// IMPORTANT: game packets are NOT reassembled back into protobuf. The game's
/// generators serialize nested messages via SerializeWithSize, which requires a
/// populated <c>size</c> field, and it is not populated when an incoming packet
/// is parsed — re-serialization fails with "Sizing mismatch: 0 != N".
/// Instead the fields are moved into flat structures
/// (<see cref="PacketMapping"/>) and encoded with our own explicit format
/// (<see cref="CapturePayloadCodec"/>).
/// </summary>
[HarmonyPatch(typeof(SystemNetworkProcess), "ProcessInBackground")]
internal static class PacketCapturePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Packet_Server packet)
    {
        if (packet == null || !CapturePipeline.Active) return;

        try
        {
            Dispatch(packet);
        }
        catch (Exception e)
        {
            // Capture must not break the game under any circumstances.
            CapturePipeline.Warn("packet capture failure id=" + packet.Id + ": " + e.Message);
        }
    }

    private static void Dispatch(Packet_Server packet)
    {
        switch (packet.Id)
        {
            case Packet_ServerIdEnum.ServerIdentification:
            {
                var identification = packet.Identification;
                if (identification == null) return;
                CapturePipeline.SetServerInfo(identification.GameVersion, identification.ServerName);
                CapturePipeline.Enqueue(() => CapturePipeline.Write(
                    CaptureRecordType.ServerIdentification,
                    CapturePayloadCodec.Encode(PacketMapping.ToPayload(identification))));
                break;
            }

            case Packet_ServerIdEnum.LevelInitialize:
            {
                var levelInitialize = packet.LevelInitialize;
                if (levelInitialize == null) return;
                CapturePipeline.Enqueue(() => CapturePipeline.Write(
                    CaptureRecordType.LevelInitialize,
                    CapturePayloadCodec.Encode(PacketMapping.ToPayload(levelInitialize))));
                break;
            }

            case Packet_ServerIdEnum.WorldMetaData:
            {
                var worldMetaData = packet.WorldMetaData;
                if (worldMetaData == null) return;
                CapturePipeline.Enqueue(() => CapturePipeline.Write(
                    CaptureRecordType.WorldMetaData,
                    CapturePayloadCodec.Encode(PacketMapping.ToPayload(worldMetaData))));
                break;
            }

            case Packet_ServerIdEnum.ServerAssets:
            {
                if (!Config().CaptureServerAssets) return;
                var assets = packet.Assets;
                if (assets?.Blocks == null) return;
                if (Config().ServerAssetsOncePerSession
                    && !CapturePipeline.TryClaimOneShot(CaptureRecordType.ServerAssets))
                {
                    return;
                }
                CapturePipeline.Enqueue(() => CapturePipeline.Write(
                    CaptureRecordType.ServerAssets,
                    CapturePayloadCodec.Encode(PacketMapping.ToPayload(assets))));
                break;
            }

            case Packet_ServerIdEnum.Chunks:
            {
                if (!Config().CaptureChunks) return;
                var chunks = packet.Chunks;
                if (chunks?.Chunks == null) return;
                CapturePipeline.Enqueue(() => WriteChunks(chunks));
                break;
            }

            case Packet_ServerIdEnum.MapChunk:
            {
                if (!Config().CaptureMapChunks) return;
                var mapChunk = packet.MapChunk;
                if (mapChunk == null) return;
                CapturePipeline.Enqueue(() => CapturePipeline.Write(
                    CaptureRecordType.MapChunk,
                    CapturePayloadCodec.Encode(PacketMapping.ToPayload(mapChunk))));
                break;
            }

            // Block entity updates outside a chunk (interaction rollback). Needed
            // because a block entity is the whole shape of a chiseled block and the
            // contents of chests/machines; without them the world loses them when built.
            case Packet_ServerIdEnum.BlockEntities:
            {
                if (!Config().CaptureChunks) return;
                var blockEntities = packet.BlockEntities;
                if (blockEntities?.BlockEntitites == null) return;
                CapturePipeline.Enqueue(() => CapturePipeline.Write(
                    CaptureRecordType.BlockEntityUpdate,
                    CapturePayloadCodec.Encode(PacketMapping.ToPayload(blockEntities))));
                break;
            }

            // MapRegion is deliberately not saved: its contents are not used when
            // building the world, and the game rebuilds regions from the seed itself.
        }
    }

    /// <summary>
    /// Written in the writer thread. Each chunk is a separate record: that way one
    /// bad chunk does not lose the whole batch, and the reader applies
    /// "last record wins" for chunks sent again.
    /// </summary>
    private static void WriteChunks(Packet_ServerChunks chunks)
    {
        // ChunksCount may be zero if the array was filled directly.
        int count = chunks.ChunksCount > 0 ? chunks.ChunksCount : chunks.Chunks.Length;
        for (int i = 0; i < count && i < chunks.Chunks.Length; i++)
        {
            var chunk = chunks.Chunks[i];
            if (chunk == null) continue;
            CapturePipeline.Write(CaptureRecordType.Chunk,
                CapturePayloadCodec.Encode(PacketMapping.ToPayload(chunk)));
        }
    }

    private static FullCaptureConfig Config() => FullCaptureModSystem.ActiveConfig;
}
