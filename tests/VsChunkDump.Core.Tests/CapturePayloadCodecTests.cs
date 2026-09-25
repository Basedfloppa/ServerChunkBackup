using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

/// <summary>
/// Codecs for capture record bodies. The structures deliberately do not depend on
/// game types, so they are tested here and not only in the collector's self-test.
/// </summary>
public class CapturePayloadCodecTests
{
    [Fact]
    public void Chunk_RoundTripsAllFields()
    {
        var payload = new ChunkPayload
        {
            X = 12,
            Y = 3,
            Z = -7,
            Compver = 2,
            Empty = false,
            Blocks = [1, 2, 3, 4, 5],
            Light = [10, 20],
            LightSat = [30],
            Liquids = [40, 41, 42],
            Moddata = [0xFF, 0x00, 0xAB],
            LightPositions = [100, 200, 300],
            DecorsPos = [7, 8],
            DecorsIds = [9, 10]
        };

        var decoded = CapturePayloadCodec.DecodeChunk(CapturePayloadCodec.Encode(payload));

        Assert.Equal(payload.X, decoded.X);
        Assert.Equal(payload.Y, decoded.Y);
        Assert.Equal(payload.Z, decoded.Z);
        Assert.Equal(payload.Compver, decoded.Compver);
        Assert.Equal(payload.Empty, decoded.Empty);
        Assert.Equal(payload.Blocks, decoded.Blocks);
        Assert.Equal(payload.Light, decoded.Light);
        Assert.Equal(payload.LightSat, decoded.LightSat);
        Assert.Equal(payload.Liquids, decoded.Liquids);
        Assert.Equal(payload.Moddata, decoded.Moddata);
        Assert.Equal(payload.LightPositions, decoded.LightPositions);
        Assert.Equal(payload.DecorsPos, decoded.DecorsPos);
        Assert.Equal(payload.DecorsIds, decoded.DecorsIds);
    }

    [Fact]
    public void Chunk_NullBlobsSurviveAsNull()
    {
        var payload = new ChunkPayload { X = 1, Empty = true, Blocks = null, Light = null, Liquids = null };

        var decoded = CapturePayloadCodec.DecodeChunk(CapturePayloadCodec.Encode(payload));

        Assert.True(decoded.Empty);
        Assert.Null(decoded.Blocks);
        Assert.Null(decoded.Light);
        Assert.Null(decoded.Liquids);
        Assert.Null(decoded.Moddata);
        Assert.Empty(decoded.LightPositions);
        Assert.Empty(decoded.DecorsPos);
    }

    [Fact]
    public void Chunk_EmptyArraysRoundTrip()
    {
        var payload = new ChunkPayload
        {
            Blocks = [],
            LightPositions = [],
            DecorsPos = [],
            DecorsIds = []
        };

        var decoded = CapturePayloadCodec.DecodeChunk(CapturePayloadCodec.Encode(payload));

        Assert.NotNull(decoded.Blocks);
        Assert.Empty(decoded.Blocks!);
        Assert.Empty(decoded.LightPositions);
    }

    [Fact]
    public void Chunk_LargeBlobRoundTrips()
    {
        var rng = new Random(42);
        var blocks = new byte[200_000];
        rng.NextBytes(blocks);

        var decoded = CapturePayloadCodec.DecodeChunk(
            CapturePayloadCodec.Encode(new ChunkPayload { Blocks = blocks }));

        Assert.Equal(blocks, decoded.Blocks);
    }

    [Fact]
    public void WorldInfo_RoundTripsIncludingMods()
    {
        var payload = new WorldInfoPayload
        {
            GameVersion = "1.22.7",
            ServerName = "Test Server",
            SavegameIdentifier = "d3e82bd6-09f9-4bfb-9b5f-d469c9cb2330",
            MapSizeX = 1024000,
            MapSizeY = 256,
            MapSizeZ = 1024000,
            Seed = 364443975,
            RequireRemapping = 1
        };
        payload.Mods.Add(new WorldInfoPayload.ModEntry("game", "1.22.7"));
        payload.Mods.Add(new WorldInfoPayload.ModEntry("survival", "1.22.7"));

        var decoded = CapturePayloadCodec.DecodeWorldInfo(CapturePayloadCodec.Encode(payload));

        Assert.Equal(payload.GameVersion, decoded.GameVersion);
        Assert.Equal(payload.ServerName, decoded.ServerName);
        Assert.Equal(payload.SavegameIdentifier, decoded.SavegameIdentifier);
        Assert.Equal(payload.MapSizeX, decoded.MapSizeX);
        Assert.Equal(payload.MapSizeY, decoded.MapSizeY);
        Assert.Equal(payload.MapSizeZ, decoded.MapSizeZ);
        Assert.Equal(payload.Seed, decoded.Seed);
        Assert.Equal(payload.RequireRemapping, decoded.RequireRemapping);
        Assert.Equal(2, decoded.Mods.Count);
        Assert.Equal("survival@1.22.7", $"{decoded.Mods[1].Id}@{decoded.Mods[1].Version}");
    }

    [Fact]
    public void WorldInfo_HandlesMissingStrings()
    {
        var decoded = CapturePayloadCodec.DecodeWorldInfo(
            CapturePayloadCodec.Encode(new WorldInfoPayload()));

        Assert.Equal("", decoded.GameVersion);
        Assert.Equal("", decoded.ServerName);
        Assert.Empty(decoded.Mods);
    }

    [Fact]
    public void LevelInit_RoundTrips()
    {
        var payload = new LevelInitPayload
        {
            ServerChunkSize = 32,
            ServerMapChunkSize = 32,
            ServerMapRegionSize = 512,
            MaxViewDistance = 512
        };

        var decoded = CapturePayloadCodec.DecodeLevelInit(CapturePayloadCodec.Encode(payload));

        Assert.Equal(payload.ServerChunkSize, decoded.ServerChunkSize);
        Assert.Equal(payload.ServerMapChunkSize, decoded.ServerMapChunkSize);
        Assert.Equal(payload.ServerMapRegionSize, decoded.ServerMapRegionSize);
        Assert.Equal(payload.MaxViewDistance, decoded.MaxViewDistance);
    }

    [Fact]
    public void WorldMeta_RoundTrips()
    {
        var payload = new WorldMetaPayload
        {
            SeaLevel = 110,
            SunBrightness = 20,
            BlockLightLevels = [0, 1, 2, 3],
            SunLightLevels = [4, 5, 6, 7]
        };

        var decoded = CapturePayloadCodec.DecodeWorldMeta(CapturePayloadCodec.Encode(payload));

        Assert.Equal(110, decoded.SeaLevel);
        Assert.Equal(20, decoded.SunBrightness);
        Assert.Equal(payload.BlockLightLevels, decoded.BlockLightLevels);
        Assert.Equal(payload.SunLightLevels, decoded.SunLightLevels);
    }

    [Fact]
    public void BlockRegistry_RoundTripsWithUnicodeCodes()
    {
        var payload = new BlockRegistryPayload();
        payload.Blocks.Add(new BlockRegistryPayload.Entry(0, "game:air"));
        payload.Blocks.Add(new BlockRegistryPayload.Entry(6770, "game:rock-granite"));
        payload.Blocks.Add(new BlockRegistryPayload.Entry(9999, "mod:blöck-wïth-ünicode"));

        var decoded = CapturePayloadCodec.DecodeBlockRegistry(CapturePayloadCodec.Encode(payload));

        Assert.Equal(3, decoded.Blocks.Count);
        Assert.Equal("game:air", decoded.Blocks[0].Code);
        Assert.Equal(6770, decoded.Blocks[1].BlockId);
        Assert.Equal("mod:blöck-wïth-ünicode", decoded.Blocks[2].Code);
    }

    [Fact]
    public void MapChunk_RoundTrips()
    {
        var rain = new byte[2048];
        var terrain = new byte[2048];
        for (int i = 0; i < 2048; i++) { rain[i] = (byte)i; terrain[i] = (byte)(i * 3); }

        var payload = new MapChunkPayload
        {
            ChunkX = -5,
            ChunkZ = 16000,
            Ymax = 90,
            RainHeightMap = rain,
            TerrainHeightMap = terrain
        };

        var decoded = CapturePayloadCodec.DecodeMapChunk(CapturePayloadCodec.Encode(payload));

        Assert.Equal(-5, decoded.ChunkX);
        Assert.Equal(16000, decoded.ChunkZ);
        Assert.Equal(90, decoded.Ymax);
        Assert.Equal(rain, decoded.RainHeightMap);
        Assert.Equal(terrain, decoded.TerrainHeightMap);
    }

    [Fact]
    public void MapChunk_NullHeightMapsSurviveAsNull()
    {
        var decoded = CapturePayloadCodec.DecodeMapChunk(
            CapturePayloadCodec.Encode(new MapChunkPayload { ChunkX = 1, ChunkZ = 2 }));

        Assert.Null(decoded.RainHeightMap);
        Assert.Null(decoded.TerrainHeightMap);
    }

    [Fact]
    public void Decode_TruncatedPayloadThrows()
    {
        var full = CapturePayloadCodec.Encode(new ChunkPayload { X = 1, Blocks = [1, 2, 3, 4, 5] });
        var truncated = full.AsSpan(0, full.Length / 2).ToArray();

        Assert.ThrowsAny<Exception>(() => CapturePayloadCodec.DecodeChunk(truncated));
    }

    [Fact]
    public void Chunk_RoundTripsThroughCaptureFile()
    {
        // Verify the 'codec + capture container' pair end to end.
        using var tmp = new TempDir("codec-capture");
        var payload = new ChunkPayload
        {
            X = 100, Y = 4, Z = -100, Compver = 2,
            Blocks = new byte[50_000],             // large enough for compression to kick in
            DecorsPos = [1, 2, 3], DecorsIds = [4, 5, 6]
        };
        new Random(7).NextBytes(payload.Blocks!);

        using (var writer = new CaptureWriter(tmp.Path, new CaptureManifest()))
        {
            writer.Write(CaptureRecordType.Chunk, CapturePayloadCodec.Encode(payload));
            writer.Write(CaptureRecordType.MapChunk, CapturePayloadCodec.Encode(new MapChunkPayload { ChunkX = 100, ChunkZ = -100 }));
        }

        var records = CaptureReader.Read(tmp.Path).ToList();
        Assert.Equal(2, records.Count);

        var chunk = CapturePayloadCodec.DecodeChunk(records[0].Payload);
        Assert.Equal(100, chunk.X);
        Assert.Equal(-100, chunk.Z);
        Assert.Equal(payload.Blocks, chunk.Blocks);
        Assert.Equal(payload.DecorsIds, chunk.DecorsIds);

        var mapChunk = CapturePayloadCodec.DecodeMapChunk(records[1].Payload);
        Assert.Equal((100, -100), (mapChunk.ChunkX, mapChunk.ChunkZ));
    }
}
