using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.Common.Database;

namespace VsVcdbsProbe;

public sealed record ProbeChunk(int X, int Y, int Z, byte[] Blob);

/// <summary>Silent logger: LoggerBase only requires LogImpl to be implemented.</summary>
public sealed class NullLogger : LoggerBase
{
    protected override void LogImpl(EnumLogType logType, string format, params object[] args)
    {
        // intentionally silent
    }
}

/// <summary>
/// Minimal .vcdbs write/read: the exact DDL from the decompiled
/// SQLiteDbConnectionv2 and key packing through the game's ChunkPos.ToChunkIndex.
/// </summary>
public static class VcdbsProbe
{
    private static bool _initialized;

    public static void Write(string path, IEnumerable<ProbeChunk> chunks)
    {
        EnsureSqlite();
        using var conn = new SqliteConnection("Data Source=" + path);
        conn.Open();

        // DDL 1:1 as in Vintagestory.Common.Database.SQLiteDbConnectionv2
        Exec(conn, "CREATE TABLE IF NOT EXISTS chunk (position integer PRIMARY KEY, data BLOB);");
        Exec(conn, "CREATE TABLE IF NOT EXISTS mapchunk (position integer PRIMARY KEY, data BLOB);");
        Exec(conn, "CREATE TABLE IF NOT EXISTS mapregion (position integer PRIMARY KEY, data BLOB);");
        Exec(conn, "CREATE TABLE IF NOT EXISTS gamedata (savegameid integer PRIMARY KEY, data BLOB);");
        Exec(conn, "CREATE TABLE IF NOT EXISTS playerdata (playerid integer PRIMARY KEY AUTOINCREMENT, playeruid TEXT, data BLOB);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS index_playeruid ON playerdata(playeruid);");

        using var tx = conn.BeginTransaction();
        foreach (var chunk in chunks)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO chunk (position, data) VALUES ($pos, $data);";
            cmd.Parameters.AddWithValue("$pos", (long)ChunkPos.ToChunkIndex(chunk.X, chunk.Y, chunk.Z, 0));
            cmd.Parameters.AddWithValue("$data", chunk.Blob);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static List<ProbeChunk> Read(string path)
    {
        EnsureSqlite();
        var result = new List<ProbeChunk>();
        using var conn = new SqliteConnection("Data Source=" + path);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT position, data FROM chunk ORDER BY position;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var pos = ChunkPos.FromChunkIndex_saveGamev2((ulong)reader.GetInt64(0));
            result.Add(new ProbeChunk(pos.X, pos.Y, pos.Z, (byte[])reader[1]));
        }
        return result;
    }

    /// <summary>
    /// Final check: read the written file with the game's own
    /// SQLiteDbConnectionv2 class, not just with raw SQLite.
    /// </summary>
    public static int GameReaderRoundTrip(string path, IReadOnlyList<ProbeChunk> expected)
    {
        EnsureSqlite();
        var logger = new NullLogger();
        using var db = new SQLiteDbConnectionv2(logger);
        var connection = (IGameDbConnection)db;

        string error = null;
        if (!connection.OpenOrCreate(path, ref error, requireWriteAccess: true,
                corruptionProtection: false, doIntegrityCheck: false))
        {
            Console.WriteLine("  FAIL: the game could not open the created .vcdbs: " + (error ?? "no description"));
            return 1;
        }

        var positions = expected.Select(c => new ChunkPos(c.X, c.Y, c.Z, 0)).ToList();
        var blobs = db.GetChunks(positions).ToList();
        Console.WriteLine($"  SQLiteDbConnectionv2.GetChunks returned {blobs.Count} blobs out of {expected.Count} requested");

        if (blobs.Count != expected.Count)
        {
            Console.WriteLine("  FAIL: the game did not read all rows");
            return 1;
        }

        // The game's reader does not guarantee order — compare as a multiset.
        var remaining = new List<byte[]>(blobs);
        int bad = 0;
        foreach (var want in expected)
        {
            int index = remaining.FindIndex(b => b.AsSpan().SequenceEqual(want.Blob));
            if (index < 0) bad++;
            else remaining.RemoveAt(index);
        }

        if (bad > 0)
        {
            Console.WriteLine($"  FAIL: {bad} blobs did not match the written ones");
            return 1;
        }

        Console.WriteLine("  OK: the game's .vcdbs reader returned exactly the blobs we wrote");
        return 0;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureSqlite()
    {
        if (_initialized) return;
        SQLitePCL.Batteries_V2.Init();
        _initialized = true;
    }
}
