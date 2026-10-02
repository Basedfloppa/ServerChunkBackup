using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Capture of the world state: the game clock and where the player stood.
///
/// These two are what let the assembled world open at the captured moment with the
/// player already in the captured place — otherwise verifying a mob means teleporting
/// to it and setting the time and the season by hand.
///
/// The clock is read from the client's calendar rather than from the calendar packet.
/// Both carry the same clock, but the client is set from the packet and then advances
/// it locally, so the client's value is that same clock read later (up to a packet
/// interval fresher). The packet is still the source of the settings that turn a clock
/// into a date: where the clock started, the hours of a day, the month length.
///
/// Everything runs on the main thread except <see cref="OnServerCalendar"/>, which is
/// called by the packet patch on the network thread and only publishes an immutable
/// settings object.
/// </summary>
internal static class WorldStateCapture
{
    private static ICoreClientAPI? _capi;
    private static ILogger? _logger;

    /// <summary>
    /// Settings of the current server's calendar. Written by the network thread, read by
    /// the main thread, so it is replaced as a whole (<see cref="Interlocked.Exchange{T}"/>)
    /// and never mutated after publication.
    /// </summary>
    private static CalendarSettings? _serverCalendar;

    /// <summary>The last snapshot taken, one tick old at most: the source for the forced write.</summary>
    private static WorldStatePayload? _last;

    private static long _lastWriteMs;
    private static long _written;
    private static long _failed;

    public static long Written => Interlocked.Read(ref _written);
    public static long Failed => Interlocked.Read(ref _failed);

    public static void Install(ICoreClientAPI capi, ILogger logger)
    {
        Uninstall();
        _capi = capi;
        _logger = logger;
    }

    public static void Uninstall()
    {
        _capi = null;
        _logger = null;
    }

    /// <summary>
    /// Calendar settings from the server (network thread). Only stored: the payload is
    /// assembled where the player position is available, on the main thread.
    /// </summary>
    public static void OnServerCalendar(Packet_ServerCalendar packet)
    {
        // The unpacking of the packet's packed floats is shared with the offline tools
        // (PacketMapping), so it is covered by their self-test as well.
        var settings = PacketMapping.ToCalendarSettings(packet);
        Interlocked.Exchange(ref _serverCalendar, settings);
    }

    /// <summary>
    /// Snapshot every tick, write at the configured interval. The snapshot has to be
    /// cheap — it is a few field reads — but the write is not free, so it is throttled.
    /// The snapshot is kept regardless of the throttle: leaving the world writes the
    /// last one, which is then a second old instead of an interval old.
    /// </summary>
    public static void Tick(long now)
    {
        if (_capi == null || !CapturePipeline.Active) return;
        if (!FullCaptureModSystem.ActiveConfig.CaptureWorldState) return;

        var payload = Snapshot();
        if (payload == null) return;
        _last = payload;

        if (now - _lastWriteMs < FullCaptureModSystem.ActiveConfig.WorldStateIntervalMs) return;
        _lastWriteMs = now;
        Enqueue(payload);
    }

    /// <summary>
    /// Write the state right now, ignoring the interval — the build command uses this so that
    /// the recorded moment is the moment of the build, not wherever the last periodic write
    /// happened to land.
    /// </summary>
    public static void CaptureNow()
    {
        if (_capi == null || !CapturePipeline.Active) return;
        if (!FullCaptureModSystem.ActiveConfig.CaptureWorldState) return;

        var payload = Snapshot();
        if (payload == null) return;

        _last = payload;
        _lastWriteMs = _capi.World.ElapsedMilliseconds;
        Enqueue(payload);
    }

    /// <summary>
    /// Re-write the last snapshot. Called when leaving the world: the player position read a
    /// moment ago is the one to keep, and reading the API while the world is being torn down
    /// is not safe. This may repeat the last periodic write — the reader keeps the last state,
    /// so a duplicate record costs nothing.
    /// </summary>
    public static void WriteLast()
    {
        var payload = _last;
        if (payload == null || !CapturePipeline.Active) return;
        Enqueue(payload);
    }

    /// <summary>
    /// Forget the state of the previous world. The next server has its own calendar, and
    /// keeping the old settings would put its clock into the previous world's calendar.
    /// </summary>
    public static void ResetSession()
    {
        _last = null;
        _lastWriteMs = 0;
        Interlocked.Exchange(ref _serverCalendar, null);
    }

    /// <summary>What is in the capture right now — for the chat command.</summary>
    public static string StatusText()
    {
        var state = _last;
        if (state == null) return "world state: not read yet";

        string position = state.HasPlayer
            ? string.Format(CultureInfo.InvariantCulture, "at {0:F1}, {1:F1}, {2:F1} (yaw {3:F2})",
                state.PlayerX, state.PlayerY, state.PlayerZ, state.PlayerYaw)
            : "player position unknown";

        string source = state.Calendar.FromServer ? "server calendar" : "client calendar (defaults)";
        string season = state.Season != null ? ", " + state.Season : "";
        string writes = $"written {Written}" + (Failed > 0 ? $", read failures {Failed}" : "");
        return $"world state: {state.ClientDate ?? state.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s"}"
             + $"{season}, {position}, from the {source}, {writes}";
    }

    private static WorldStatePayload? Snapshot()
    {
        var capi = _capi;
        if (capi == null) return null;

        try
        {
            IGameCalendar? calendar = capi.World?.Calendar;
            if (calendar == null) return null;

            var settings = Volatile.Read(ref _serverCalendar);
            var payload = new WorldStatePayload
            {
                // The clock and the date are read in one go from the same calendar, so the
                // log can never name a date other than the one this clock puts the world at.
                TotalSeconds = (long)Math.Round(calendar.TotalHours * 3600.0),
                Calendar = settings == null ? FromClientCalendar(calendar) : Clone(settings),
                ClientDate = calendar.PrettyDate()
            };

            AddPlayer(capi, calendar, payload);
            return payload;
        }
        catch (Exception e)
        {
            // A missing snapshot costs the time and the spawn point, not the world: the
            // capture must never take the game down with it.
            if (Interlocked.Increment(ref _failed) <= 3)
            {
                _logger?.Warning("World state not read ({0}) — the world will be built without time and spawn point.",
                    e.Message);
            }
            return null;
        }
    }

    /// <summary>
    /// Settings when the server's calendar packet was not seen. The day length and the
    /// month length are still the server's (the client is told both), but the time speed
    /// modifiers are not exposed by the client API, so the default ones are assumed.
    ///
    /// The world's start time is recomputed the way the game does it
    /// (<c>SaveGame.GetTotalGameSecondsStart</c>): it depends only on the days of a month,
    /// which is why the missing packet costs nothing here either.
    /// </summary>
    private static CalendarSettings FromClientCalendar(IGameCalendar calendar)
    {
        int daysPerMonth = Math.Max(1, calendar.DaysPerMonth);
        return new CalendarSettings
        {
            TotalSecondsStart = 28800 + 86400L * daysPerMonth * 4,
            HoursPerDay = calendar.HoursPerDay,
            DaysPerMonth = daysPerMonth,
            CalendarSpeedMul = calendar.CalendarSpeedMul,
            TimeSpeedModifiers = [new CalendarSettings.TimeSpeedModifier("baseline", 60f)],
            FromServer = false
        };
    }

    /// <summary>
    /// A copy. The published instance belongs to the network thread, and the payload is
    /// encoded on the writer thread — sharing one object between them would be a race.
    /// </summary>
    private static CalendarSettings Clone(CalendarSettings source) => new()
    {
        TotalSecondsStart = source.TotalSecondsStart,
        HoursPerDay = source.HoursPerDay,
        DaysPerMonth = source.DaysPerMonth,
        CalendarSpeedMul = source.CalendarSpeedMul,
        TimeSpeedModifiers = [.. source.TimeSpeedModifiers],
        FromServer = source.FromServer
    };

    private static void AddPlayer(ICoreClientAPI capi, IGameCalendar calendar, WorldStatePayload payload)
    {
        EntityPlayer? player = capi.World?.Player?.Entity;
        EntityPos? pos = player?.Pos;
        if (pos == null) return;

        if (!double.IsFinite(pos.X) || !double.IsFinite(pos.Y) || !double.IsFinite(pos.Z)) return;

        payload.HasPlayer = true;
        payload.PlayerX = pos.X;
        payload.PlayerY = pos.Y;
        payload.PlayerZ = pos.Z;
        payload.PlayerYaw = pos.Yaw;

        // The season depends on latitude and hemisphere, so it is asked for at the
        // player's position instead of being derived from the date.
        payload.Season = calendar.GetSeason(pos.AsBlockPos).ToString();
    }

    private static void Enqueue(WorldStatePayload payload)
    {
        if (!CapturePipeline.Active) return;

        // Encoding happens on the writer thread: the patch and the tick must stay cheap.
        CapturePipeline.Enqueue(() => CapturePipeline.Write(
            CaptureRecordType.WorldState, CapturePayloadCodec.Encode(payload)));
        Interlocked.Increment(ref _written);
    }
}
