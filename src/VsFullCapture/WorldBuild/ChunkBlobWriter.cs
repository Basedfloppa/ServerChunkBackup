using System.Text;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Manual assembly of the <c>chunk</c> row blob — in the same format the game
/// writes with <c>ServerChunk.FastSerialize</c> (verified against the 1.22.7 decompile).
///
/// Why not <c>chunk.ToBytes()</c>: the game takes block entities from the live
/// <c>BlockEntity</c> collection and serializes them via <c>ToTreeAttributes</c>.
/// Such objects cannot be created without a running world and without the class
/// registry, and in a save they are stored simply as "class name + TreeAttribute" —
/// exactly what comes over the network. So we write the blob ourselves:
///
///   1  blocksCompressed     2  lightCompressed      3  lightSatCompressed
///   7  blockEntitiesCount   8  blockEntities[]      9  moddata
///   10 lightPositions       12 gameVersionCreated   13 emptyBeforeSave
///   14 decors (index3d,blockId)                      15 savedCompressionVersion
///   16 liquidsCompressed
///
/// Fields that are absent are not written at all (the game serializer does the
/// same: it skips zeros and nulls, and the reader gets default values).
/// </summary>
public static class ChunkBlobWriter
{
    /// <summary>chunkdataVersion = 2 — the current format (zstd).</summary>
    public const int CompressionVersion = 2;

    public static byte[] Write(
        byte[]? blocks,
        byte[]? light,
        byte[]? lightSat,
        byte[]? liquids,
        IReadOnlyList<byte[]> blockEntities,
        IDictionary<string, byte[]>? moddata,
        IEnumerable<int>? lightPositions,
        IReadOnlyList<(int Index3d, int BlockId)>? decors,
        string gameVersionCreated,
        bool empty)
    {
        using var ms = new FastMemoryStream();
        // The game FastSerializer annotations do not reflect that null means
        // "do not write the field" — that is its documented behavior.
        FastSerializer.Write(ms, 1, blocks!);
        FastSerializer.Write(ms, 2, light!);
        FastSerializer.Write(ms, 3, lightSat!);

        if (blockEntities.Count > 0) FastSerializer.Write(ms, 7, blockEntities.Count);
        FastSerializer.Write(ms, 8, blockEntities);

        FastSerializer.Write(ms, 9, moddata!);
        if (lightPositions != null) FastSerializer.Write(ms, 10, lightPositions);
        FastSerializer.Write(ms, 12, gameVersionCreated);
        FastSerializer.Write(ms, 13, empty);

        if (decors is { Count: > 0 })
        {
            // The game's FastSerializeDecors writes int32 pairs in a row, with no tag per pair.
            FastSerializer.WriteTagLengthDelim(ms, 14, decors.Count * 8);
            foreach (var (index3d, blockId) in decors)
            {
                ms.WriteInt32(index3d);
                ms.WriteInt32(blockId);
            }
        }

        FastSerializer.Write(ms, 15, CompressionVersion);
        FastSerializer.Write(ms, 16, liquids!);
        return ms.ToArray();
    }

    /// <summary>
    /// Write a block entity in the form the game reads it when loading a chunk:
    /// <c>BinaryReader.ReadString()</c> of the class name, then the TreeAttribute
    /// bytes (ServerChunk.FromBytes → AfterDeserialization).
    /// </summary>
    public static byte[] ToSaveEntry(string classname, byte[] treeBytes)
    {
        using var ms = new MemoryStream(treeBytes.Length + classname.Length + 8);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(classname);
        w.Write(treeBytes);
        w.Flush();
        return ms.ToArray();
    }
}
