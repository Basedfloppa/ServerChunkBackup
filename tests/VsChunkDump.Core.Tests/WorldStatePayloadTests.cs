using System.Text;
using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

/// <summary>
/// The world state in the capture: the game clock and where the player stood. The builder
/// writes them into the save as TotalGameSeconds and DefaultSpawn — the time of day, the
/// day of the year and the season all follow from that one clock value, so without this
/// record an assembled world opens at its own midnight and verifying anything means
/// teleporting to it by hand.
/// </summary>
public class WorldStatePayloadTests
{
    private static CaptureManifest Manifest() => new() { GameVersion = "1.22.7" };

    private static WorldStatePayload Sample() => new()
    {
        TotalSeconds = 3139200 + 86400 * 3 + 7 * 3600,
        Calendar = new CalendarSettings
        {
            TotalSecondsStart = 3139200,
            HoursPerDay = 20f,
            DaysPerMonth = 9,
            CalendarSpeedMul = 0.25f,
            TimeSpeedModifiers = [new CalendarSettings.TimeSpeedModifier("baseline", 60f)],
            FromServer = true
        },
        ClientDate = "July 4, 1387, 07:00",
        Season = "Summer",
        HasPlayer = true,
        PlayerX = 10.5,
        PlayerY = 68.0,
        PlayerZ = -12.5,
        PlayerYaw = 1.25f
    };

    [Fact]
    public void WorldStateRoundTrips()
    {
        var state = Sample();

        var decoded = CapturePayloadCodec.DecodeWorldState(CapturePayloadCodec.Encode(state));

        Assert.Equal(state.TotalSeconds, decoded.TotalSeconds);
        Assert.Equal(3139200, decoded.Calendar.TotalSecondsStart);
        Assert.Equal(20f, decoded.Calendar.HoursPerDay);
        Assert.Equal(9, decoded.Calendar.DaysPerMonth);
        Assert.Equal(0.25f, decoded.Calendar.CalendarSpeedMul);
        Assert.True(decoded.Calendar.FromServer);
        var modifier = Assert.Single(decoded.Calendar.TimeSpeedModifiers);
        Assert.Equal("baseline", modifier.Name);
        Assert.Equal(60f, modifier.Speed);
        Assert.Equal("July 4, 1387, 07:00", decoded.ClientDate);
        Assert.Equal("Summer", decoded.Season);
        Assert.True(decoded.HasPlayer);
        Assert.Equal(10.5, decoded.PlayerX);
        Assert.Equal(68.0, decoded.PlayerY);
        Assert.Equal(-12.5, decoded.PlayerZ);
        Assert.Equal(1.25f, decoded.PlayerYaw);
    }

    [Fact]
    public void WorldStateWithSeveralSpeedModifiersRoundTrips()
    {
        var state = Sample();
        state.Calendar.TimeSpeedModifiers =
        [
            new CalendarSettings.TimeSpeedModifier("baseline", 60f),
            new CalendarSettings.TimeSpeedModifier("temporalstorm", 0.5f)
        ];

        var decoded = CapturePayloadCodec.DecodeWorldState(CapturePayloadCodec.Encode(state));

        Assert.Equal(2, decoded.Calendar.TimeSpeedModifiers.Count);
        Assert.Equal(0.5f, decoded.Calendar.TimeSpeedModifiers[1].Speed);
    }

    [Fact]
    public void WorldStateWithoutAModifierListRoundTrips()
    {
        var state = Sample();
        state.Calendar.TimeSpeedModifiers = [];

        var decoded = CapturePayloadCodec.DecodeWorldState(CapturePayloadCodec.Encode(state));

        Assert.Empty(decoded.Calendar.TimeSpeedModifiers);
    }

    [Fact]
    public void WorldStateWithoutAPlayerStillCarriesTheClock()
    {
        // The capture can end while no player entity exists (left the world, no character
        // created yet). The clock is still worth keeping: the world would open at the right
        // moment even without a spawn point.
        var state = Sample();
        state.HasPlayer = false;
        state.ClientDate = null;
        state.Season = null;

        var decoded = CapturePayloadCodec.DecodeWorldState(CapturePayloadCodec.Encode(state));

        Assert.False(decoded.HasPlayer);
        Assert.Equal(state.TotalSeconds, decoded.TotalSeconds);
        Assert.Null(decoded.ClientDate);
        Assert.Null(decoded.Season);
    }

    [Fact]
    public void WorldStateWithoutTheDisplayStringsStillDecodes()
    {
        // The format only grows by appending fields to the tail, so a record written before
        // the two display strings existed must still yield the clock and the spawn point:
        // refusing it would cost an existing capture its time and its spawn point.
        var state = Sample();

        var decoded = CapturePayloadCodec.DecodeWorldState(WithoutDisplayStrings(state));

        Assert.Equal(state.TotalSeconds, decoded.TotalSeconds);
        Assert.Equal(state.PlayerX, decoded.PlayerX);
        Assert.True(decoded.HasPlayer);
        Assert.Null(decoded.ClientDate);
        Assert.Null(decoded.Season);
    }

    [Fact]
    public void RecordTypeDoesNotRepeatTheExistingOnes()
    {
        var types = Enum.GetValues<CaptureRecordType>().Cast<ushort>().ToList();

        Assert.Equal(types.Count, types.Distinct().Count());
        Assert.Equal((ushort)11, (ushort)CaptureRecordType.WorldState);
    }

    [Fact]
    public void LastWorldStateWins()
    {
        // The state is re-written while the world runs; the builder needs the last one.
        using var dir = new TempDir("worldstate");
        var early = Sample();
        early.PlayerX = 1;
        var late = Sample();
        late.PlayerX = 2;
        late.TotalSeconds = 999;

        Write(dir, [
            (CaptureRecordType.WorldState, CapturePayloadCodec.Encode(early)),
            (CaptureRecordType.WorldState, CapturePayloadCodec.Encode(late))
        ]);

        var model = CaptureModel.Load(dir.Path);

        Assert.NotNull(model.WorldState);
        Assert.Equal(2, model.WorldState.PlayerX);
        Assert.Equal(999, model.WorldState.TotalSeconds);
    }

    [Fact]
    public void CaptureWithoutAWorldStateStillLoads()
    {
        // Captures made before this record existed: the builder has to cope with null.
        using var dir = new TempDir("worldstate");
        Write(dir, [
            (CaptureRecordType.Chunk, CapturePayloadCodec.Encode(new ChunkPayload { X = 1, Y = 2, Z = 3, Blocks = [1] }))
        ]);

        var model = CaptureModel.Load(dir.Path);

        Assert.Single(model.Chunks);
        Assert.Null(model.WorldState);
    }

    private static void Write(TempDir dir, (CaptureRecordType Type, byte[] Payload)[] records)
    {
        using var writer = new CaptureWriter(dir.Path, Manifest());
        foreach (var (type, payload) in records) writer.Write(type, payload);
    }

    /// <summary>
    /// A record as an older build wrote it: the fixed part, without the display strings that
    /// were appended later. Written out by hand on purpose — a copy of the format that does
    /// not follow the encoder, which is what makes it an old record.
    /// </summary>
    private static byte[] WithoutDisplayStrings(WorldStatePayload s)
    {
        using var ms = new MemoryStream(128);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(s.TotalSeconds);
        w.Write(s.Calendar.TotalSecondsStart);
        w.Write(s.Calendar.HoursPerDay);
        w.Write(s.Calendar.DaysPerMonth);
        w.Write(s.Calendar.CalendarSpeedMul);
        w.Write(s.Calendar.FromServer);
        w.Write(s.Calendar.TimeSpeedModifiers.Count);
        foreach (var modifier in s.Calendar.TimeSpeedModifiers)
        {
            w.Write(modifier.Name);
            w.Write(modifier.Speed);
        }
        w.Write(s.HasPlayer);
        w.Write(s.PlayerX);
        w.Write(s.PlayerY);
        w.Write(s.PlayerZ);
        w.Write(s.PlayerYaw);
        w.Flush();
        return ms.ToArray();
    }
}
