using System.Reflection;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Util;
using Vintagestory.Common.Database;

namespace VsVcdbsProbe;

/// <summary>
/// Inspect a real .vcdbs: table layout, the range of chunk coordinates, and a check
/// that the ChunkPos key encodes/decodes losslessly on real data.
/// Opens the file read-only.
/// </summary>
public static class RealSaveInspect
{
    public static int Run(string path, int sampleLimit)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("File not found: " + path);
            return 2;
        }

        SQLitePCL.Batteries_V2.Init();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();

        Console.WriteLine($"Savegame: {path}");
        Console.WriteLine($"Size: {new FileInfo(path).Length / 1024.0 / 1024.0:F1} MiB");
        Console.WriteLine();

        Console.WriteLine("Tables:");
        foreach (string table in new[] { "chunk", "mapchunk", "mapregion", "gamedata", "playerdata" })
        {
            long count = Count(conn, table);
            long bytes = TableBytes(conn, table);
            Console.WriteLine($"  {table,-11} rows {count,10:N0}   data ≈ {bytes / 1024.0 / 1024.0:F1} MiB");
        }
        Console.WriteLine();

        InspectGameData(conn);
        InspectChunkKeys(conn, sampleLimit);

        return 0;
    }

    private static void InspectGameData(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM gamedata LIMIT 1";
        var blob = cmd.ExecuteScalar() as byte[];
        if (blob == null)
        {
            Console.WriteLine("gamedata: empty");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"gamedata: {blob.Length} bytes");
        try
        {
            var save = SerializerUtil.Deserialize<SaveGame>(blob);
            foreach (var field in typeof(SaveGame).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string name = field.Name;
                bool interesting = name.StartsWith("MapSize")
                                   || name.Contains("Seed")
                                   || name.Contains("WorldName")
                                   || name.Contains("Version")
                                   || name.Contains("Identifier")
                                   || name.Contains("PlayStyle");
                if (!interesting) continue;

                object value = field.GetValue(save);
                string text = value is System.Collections.IDictionary dict ? $"<dictionary, {dict.Count} entries>" : value?.ToString() ?? "null";
                Console.WriteLine($"  {name,-28} = {text}");
            }

            if (save.ModData != null)
            {
                Console.WriteLine($"  ModData: {save.ModData.Count} keys: {string.Join(", ", save.ModData.Keys.Take(12))}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("  could not parse SaveGame: " + e.Message);
        }
        Console.WriteLine();
    }

    private static void InspectChunkKeys(SqliteConnection conn, int limit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT position FROM chunk LIMIT {limit}";
        using var reader = cmd.ExecuteReader();

        long n = 0, negativeX = 0, negativeZ = 0, roundTripFailures = 0;
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue, maxDim = int.MinValue;
        var badSamples = new List<string>();

        while (reader.Read())
        {
            ulong key = unchecked((ulong)reader.GetInt64(0));
            var pos = ChunkPos.FromChunkIndex_saveGamev2(key);
            n++;

            if (pos.X < 0) negativeX++;
            if (pos.Z < 0) negativeZ++;

            minX = Math.Min(minX, pos.X); maxX = Math.Max(maxX, pos.X);
            minZ = Math.Min(minZ, pos.Z); maxZ = Math.Max(maxZ, pos.Z);
            minY = Math.Min(minY, pos.Y); maxY = Math.Max(maxY, pos.Y);
            maxDim = Math.Max(maxDim, pos.Dimension);

            // The key must round-trip back to its original value.
            if (pos.ToChunkIndex() != key)
            {
                roundTripFailures++;
                if (badSamples.Count < 5) badSamples.Add($"{pos.X},{pos.Y},{pos.Z},dim{pos.Dimension}");
            }
        }

        Console.WriteLine($"Chunk keys checked: {n:N0} (limit {limit:N0})");
        if (n == 0) { Console.WriteLine(); return; }

        Console.WriteLine($"  X: {minX}..{maxX}    Z: {minZ}..{maxZ}    Y sections: {minY}..{maxY}    max dim: {maxDim}");
        Console.WriteLine($"  negative X: {negativeX}, Z: {negativeZ}");
        Console.WriteLine($"  keys that do not survive the round-trip: {roundTripFailures}"
                          + (badSamples.Count > 0 ? $" (for example {string.Join("; ", badSamples)})" : ""));
        Console.WriteLine(roundTripFailures == 0
            ? "  CONCLUSION: ChunkPos packing works correctly on real data"
            : "  CONCLUSION: ChunkPos packing loses data — this must be taken into account");
        Console.WriteLine();
    }

    private static long Count(SqliteConnection conn, string table)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return -1; // older savegames may not have the table
        }
    }

    private static long TableBytes(SqliteConnection conn, string table)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COALESCE(SUM(LENGTH(data)),0) FROM {table}";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
    }
}
