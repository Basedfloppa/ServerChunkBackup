using System.Collections.Concurrent;
using System.IO.Compression;
using Vintagestory.API.Common;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Capture pipeline: the Harmony patch puts "jobs" on a queue, and a dedicated
/// thread executes them — serializes the packet and writes a record to the file.
///
/// This is essential: the patch runs on the BACKGROUND NETWORK thread
/// (<c>SystemNetworkProcess.OnSeperateThreadGameTick</c>), so serialization and
/// disk I/O are not allowed there — they would slow down packet reception.
///
/// The packet objects themselves can safely be handed to another thread: their
/// byte fields are created anew for each packet (<c>ProtocolParser.ReadBytes</c>
/// does <c>new byte[]</c>), and are not slices of a reused receive buffer.
/// </summary>
internal static class CapturePipeline
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static readonly HashSet<CaptureRecordType> OneShotDone = [];

    private static CaptureWriter? _writer;
    private static ILogger? _logger;
    private static Thread? _worker;
    private static volatile bool _running;
    private static FullCaptureConfig _config = new();

    private static long _queued, _written, _dropped, _failed;

    /// <summary>Whether the server identification packet was caught (otherwise world parameters come from the API).</summary>
    public static bool ServerInfoSeen { get; private set; }

    /// <summary>
    /// Capture is paused. Used for singleplayer: the server is local there,
    /// there is nothing to download, and the packet stream and disk writes only
    /// burn resources.
    /// </summary>
    public static bool Paused { get; set; }

    public static bool Active => _running && _writer != null && !Paused;
    public static long Queued => Interlocked.Read(ref _queued);
    public static long Written => Interlocked.Read(ref _written);
    public static long Dropped => Interlocked.Read(ref _dropped);
    public static long Failed => Interlocked.Read(ref _failed);
    public static string Root => _writer?.Root ?? "";

    public static void Start(CaptureWriter writer, FullCaptureConfig config, ILogger logger)
    {
        Stop();
        _writer = writer;
        _config = config;
        _logger = logger;
        lock (OneShotDone) OneShotDone.Clear();
        ServerInfoSeen = false;
        Paused = false;
        _running = true;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "vsfullcapture-writer",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
    }

    public static void Stop()
    {
        _running = false;
        var worker = _worker;
        _worker = null;
        if (worker != null)
        {
            try { worker.Join(TimeSpan.FromSeconds(20)); } catch (Exception) { /* exiting the game */ }
        }
        // Write out whatever is left in the queue.
        Drain();
        _writer = null;
    }

    /// <summary>Queue a write job. Called from the network thread — queue only.</summary>
    public static void Enqueue(Action job)
    {
        if (!Active) return;
        if (Queue.Count >= _config.MaxQueueDepth)
        {
            long dropped = Interlocked.Increment(ref _dropped);
            // This must not go unsaid: queue overflow = holes in the assembled world.
            if (dropped == 1 || dropped % 1000 == 0)
            {
                _logger?.Warning(
                    "write queue overflowed — records lost: {0}. "
                    + "Chunks in that part of the world may not be saved.", dropped);
            }
            return;
        }
        Queue.Enqueue(job);
        Interlocked.Increment(ref _queued);
    }

    /// <summary>
    /// For records that must not be written twice (assets). Returns false
    /// if such a record has already been made for the current server.
    /// </summary>
    public static bool TryClaimOneShot(CaptureRecordType type)
    {
        lock (OneShotDone)
        {
            return OneShotDone.Add(type);
        }
    }

    /// <summary>
    /// Clear the "already written" marks. Called when leaving the world: you can
    /// join several servers in one game run, and each has its own block registry.
    /// Without the reset, the registry only ended up in the first server's
    /// capture, and without it block ids get mixed up in the assembled world.
    /// </summary>
    public static void ClearOneShots()
    {
        lock (OneShotDone) OneShotDone.Clear();
    }

    private static void WorkerLoop()
    {
        while (_running)
        {
            if (Queue.TryDequeue(out var job))
            {
                RunJob(job);
            }
            else
            {
                Thread.Sleep(5);
            }
        }
    }

    private static void Drain()
    {
        while (Queue.TryDequeue(out var job)) RunJob(job);
    }

    private static void RunJob(Action job)
    {
        try
        {
            job();
            Interlocked.Increment(ref _written);
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref _failed);
            _logger?.Error("Write error: {0}", e);
        }
    }

    /// <summary>Write a ready payload (called in the writer thread).</summary>
    public static void Write(CaptureRecordType type, byte[] payload)
    {
        var writer = _writer;
        if (writer == null) return;
        writer.Write(type, payload);

        if (_config.VerboseLogging && type != CaptureRecordType.Chunk && type != CaptureRecordType.MapChunk)
        {
            _logger?.Notification("{0}: {1} bytes", CaptureFormat.Describe(type), payload.Length);
        }
    }

    public static void Flush()
    {
        try { _writer?.FlushStream(); } catch (Exception e) { _logger?.Warning("flush: {0}", e.Message); }
    }

    public static void SaveManifest()
    {
        try { _writer?.SaveManifest(); } catch (Exception e) { _logger?.Warning("manifest: {0}", e.Message); }
    }

    /// <summary>Write server details from the identification packet into the manifest.</summary>
    public static void SetServerInfo(string? gameVersion, string? serverName)
    {
        ServerInfoSeen = true;
        var manifest = _writer?.Manifest;
        if (manifest == null) return;
        if (!string.IsNullOrEmpty(gameVersion)) manifest.GameVersion = gameVersion;
        if (!string.IsNullOrEmpty(serverName)) manifest.ServerName = serverName;
    }

    /// <summary>
    /// Clear the "identification packet caught" mark. Called when leaving the
    /// world: the next connection has its own packet, and the fallback must work
    /// again if it is missed once more.
    /// </summary>
    public static void ResetServerInfo() => ServerInfoSeen = false;

    /// <summary>Warning from the patch (the patch itself must not log directly).</summary>
    public static void Warn(string message) => _logger?.Warning("" + message);

    public static CompressionLevel CompressionLevelFor(int value) => value switch
    {
        1 => CompressionLevel.Optimal,
        2 => CompressionLevel.NoCompression,
        _ => CompressionLevel.Fastest
    };

    public static string StatusText()
    {
        string root = Root;
        return $"records written {Written}, queued {Queued}, dropped {Dropped}, errors {Failed}"
             + (root.Length > 0 ? $" | directory: {root}" : "");
    }
}
