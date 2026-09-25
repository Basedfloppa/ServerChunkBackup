using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

/// <summary>
/// Block entities in the capture format: without them chiseled blocks and everything
/// drawn by a block entity are lost when the world is assembled.
/// </summary>
public class BlockEntityPayloadTests
{
    private static BlockEntityPayload Sample(string classname, int x, int y, int z, params byte[] data) => new()
    {
        Classname = classname,
        X = x,
        Y = y,
        Z = z,
        Data = data
    };

    [Fact]
    public void ChunkRoundTripsBlockEntities()
    {
        var chunk = new ChunkPayload
        {
            X = 1,
            Y = 2,
            Z = 3,
            Blocks = [0xF0, 0xFF, 0xFF, 0xFF],
            BlockEntities =
            [
                Sample("BlockEntityChisel", 40, 71, 40, 1, 2, 3, 4, 5),
                Sample("BlockEntityCrate", 41, 71, 40, 9, 8, 7)
            ]
        };

        var decoded = CapturePayloadCodec.DecodeChunk(CapturePayloadCodec.Encode(chunk));

        Assert.Equal(2, decoded.BlockEntities.Count);
        Assert.Equal("BlockEntityChisel", decoded.BlockEntities[0].Classname);
        Assert.Equal((40, 71, 40), (decoded.BlockEntities[0].X, decoded.BlockEntities[0].Y, decoded.BlockEntities[0].Z));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, decoded.BlockEntities[0].Data);
        Assert.Equal("BlockEntityCrate", decoded.BlockEntities[1].Classname);
        Assert.Equal(new byte[] { 9, 8, 7 }, decoded.BlockEntities[1].Data);
    }

    [Fact]
    public void OldChunkRecordsWithoutBlockEntitiesStillDecode()
    {
        // The format evolves by appending fields to the tail: captures
        // taken before block entities existed simply do not have the field.
        var chunk = new ChunkPayload { X = 1, Y = 2, Z = 3, Blocks = [1, 2, 3], DecorsPos = [7], DecorsIds = [9] };
        byte[] full = CapturePayloadCodec.Encode(chunk);

        // Cut off exactly the written block entity tail (the 4-byte counter = -1).
        byte[] withoutTail = full[..^4];

        var decoded = CapturePayloadCodec.DecodeChunk(withoutTail);

        Assert.Empty(decoded.BlockEntities);
        Assert.Equal([7], decoded.DecorsPos);
        Assert.Equal([9], decoded.DecorsIds);
    }

    [Fact]
    public void UpdateRoundTrips()
    {
        var update = new BlockEntityUpdatePayload
        {
            BlockEntities = [Sample("BlockEntityChisel", 40, 71, 40, 42)]
        };

        var decoded = CapturePayloadCodec.DecodeBlockEntityUpdate(CapturePayloadCodec.Encode(update));

        Assert.Single(decoded.BlockEntities);
        Assert.Equal("BlockEntityChisel", decoded.BlockEntities[0].Classname);
        Assert.Equal(42, decoded.BlockEntities[0].Data[0]);
    }

    [Fact]
    public void UpdatesOverrideChunkBlockEntitiesAndAddMissingOnes()
    {
        var model = new CaptureModel();
        var chunk = new ChunkPayload
        {
            X = 0,
            Y = 2,
            Z = 0,
            BlockEntities = [Sample("BlockEntityChisel", 5, 64 + 1, 5, 1)]
        };

        // An update of the same block entity plus another one the chunk did not know about.
        model.BlockEntityUpdates[(5, 65, 5)] = Sample("BlockEntityChisel", 5, 65, 5, 2);
        model.BlockEntityUpdates[(6, 65, 5)] = Sample("BlockEntityCrate", 6, 65, 5, 3);

        var merged = model.BlockEntitiesFor(chunk);

        Assert.Equal(2, merged.Count);
        Assert.Equal(2, merged.Single(be => be.X == 5).Data[0]);
        Assert.Equal(3, merged.Single(be => be.X == 6).Data[0]);
    }

    [Fact]
    public void UpdatesFromOtherChunksAreNotAdded()
    {
        var model = new CaptureModel();
        var chunk = new ChunkPayload { X = 0, Y = 0, Z = 0 };

        model.BlockEntityUpdates[(33, 5, 5)] = Sample("BlockEntityCrate", 33, 5, 5, 1);  // chunk X=1
        model.BlockEntityUpdates[(5, 5, 33)] = Sample("BlockEntityCrate", 5, 5, 33, 2);  // chunk Z=1
        model.BlockEntityUpdates[(5, 40, 5)] = Sample("BlockEntityCrate", 5, 40, 5, 3);  // section Y=1

        Assert.Empty(model.BlockEntitiesFor(chunk));
    }
}
