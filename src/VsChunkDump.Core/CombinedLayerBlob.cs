using System.Buffers.Binary;

namespace VsChunkDump.Core;

/// <summary>
/// Zstd as the game understands it. Extracted into an interface so that Core stays
/// free of a dependency on the game assemblies: the mod and the offline tool plug in
/// <c>Vintagestory.Common.CompressionZSTD</c>, and the tests a simple stub.
/// </summary>
public interface IZstdCodec
{
    byte[] Decompress(byte[] data, int offset, int length);
    byte[] Compress(byte[] data, int length);
}

public readonly struct PaletteTranslation
{
    /// <summary>How many entries are in the palette.</summary>
    public int Entries { get; init; }

    /// <summary>How many entries actually changed.</summary>
    public int Changed { get; init; }

    /// <summary>How many entries went to the fallback id (the blocks are not in this world).</summary>
    public int ReplacedByFallback { get; init; }

    /// <summary>The palette in the blob was zstd-compressed.</summary>
    public bool WasCompressed { get; init; }
}

/// <summary>
/// Editing the palette directly inside a block or liquid layer blob.
///
/// 'combined' blob layout (verified against the decompiled Compression.cs /
/// ArrayConvert.cs of game version 1.22.7):
///
///   offset 0      : int32 LE N
///                   N == 0 → the layer is empty, the blob is exactly 4 zero bytes
///                   N &lt; 0 → the palette is stored as RAW int32, |N| = count*4 bytes
///                   N &gt; 0 → the palette is a zstd frame N bytes long
///   offset 4      : palette (count int32)
///   offset 4+|N|  : zstd frame with the bit planes (bitsize * 1024 int32)
///
/// The bit planes store palette indices, NOT block ids. That is why translating
/// ids only edits the palette: O(number of palette entries), not O(32768
/// blocks). The bit planes are copied byte for byte and stay valid:
/// the entry count does not change, so bitsize does not either.
/// </summary>
public static class CombinedLayerBlob
{
    public const int HeaderSize = 4;

    /// <summary>
    /// Blob header value: the signed palette length
    /// (0 — the layer is empty, &lt;0 — raw palette, &gt;0 — compressed). 0 for a too
    /// short blob.
    /// </summary>
    public static int ReadLengthCode(byte[]? blob)
        => blob == null || blob.Length < HeaderSize ? 0 : BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(0, 4));

    /// <summary>
    /// Translate the ids in a layer palette. Returns false if there is nothing to translate
    /// (empty layer, one entry, all ids already in place) or the blob cannot be parsed —
    /// then <paramref name="result"/> stays the original blob, and the world simply
    /// keeps it as is.
    /// </summary>
    public static bool TryTranslatePalette(
        byte[]? blob,
        IReadOnlyDictionary<int, int> map,
        int fallbackId,
        IZstdCodec? codec,
        out byte[] result,
        out PaletteTranslation stats)
    {
        stats = default;
        if (blob == null || blob.Length < HeaderSize)
        {
            result = blob ?? [];
            return false;
        }

        // Empty layer: the game reads these as 'no layer'.
        if (!TryReadPalette(blob, codec, out int[]? palette, out bool compressed, out int bitPlaneOffset))
        {
            result = blob;
            return false;
        }

        int count = palette!.Length;
        // One entry — for the game that is the same empty layer (DecompressCombined
        // returns null when count <= 1). There is nothing to change there.
        if (count <= 1)
        {
            result = blob;
            return false;
        }

        var translated = new int[count];
        int changed = 0, fallback = 0;
        for (int i = 0; i < count; i++)
        {
            int id = palette[i];
            int mapped = map.TryGetValue(id, out int local) ? local : fallbackId;
            translated[i] = mapped;
            if (mapped != id) changed++;
            if (mapped == fallbackId && id != fallbackId) fallback++;
        }

        if (changed == 0)
        {
            result = blob;
            return false;
        }

        stats = new PaletteTranslation
        {
            Entries = count,
            Changed = changed,
            ReplacedByFallback = fallback,
            WasCompressed = compressed
        };

        var paletteBytes = new byte[count * 4];
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(paletteBytes.AsSpan(i * 4, 4), translated[i]);

        if (!compressed)
        {
            // Raw palette of the same length: edit in place, the blob does not grow.
            result = (byte[])blob.Clone();
            paletteBytes.CopyTo(result, HeaderSize);
            return true;
        }

        if (codec == null)
        {
            // The palette was compressed and there is nothing to compress it back with. Leave it as is:
            // corrupting the blob is worse than not translating the ids.
            result = blob;
            stats = default;
            return false;
        }

        // The palette was compressed: its length will change after recompression, so
        // rewrite the header and carry the bit planes over as they are.
        byte[] compressedPalette = codec.Compress(paletteBytes, paletteBytes.Length);
        int bitPlaneLength = blob.Length - bitPlaneOffset;
        var rebuilt = new byte[HeaderSize + compressedPalette.Length + bitPlaneLength];
        BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(0, 4), compressedPalette.Length);
        compressedPalette.CopyTo(rebuilt, HeaderSize);
        Array.Copy(blob, bitPlaneOffset, rebuilt, HeaderSize + compressedPalette.Length, bitPlaneLength);
        result = rebuilt;
        return true;
    }

    /// <summary>
    /// Parse a layer palette. Returns false for an empty or corrupt blob.
    /// </summary>
    /// <summary>
    /// The block id at one position of a layer, read the way the game reads it:
    /// the bit planes hold palette indices, bitsize bits per index, plane by plane.
    ///
    /// Needed to answer "which block is under this block entity" without the game:
    /// an entity whose block is not under it must not be written (the game deletes
    /// such entities on load, and a class that reads its block throws on the client).
    /// <paramref name="index"/> is
    /// <c>((y % 32) * 32 + z % 32) * 32 + x % 32</c> — the game's own formula.
    /// </summary>
    public static bool TryReadBlockId(byte[]? blob, IZstdCodec? codec, int index, out int blockId)
    {
        blockId = 0;
        if (index < 0 || index >= 32768) return false;
        if (!TryReadPalette(blob, codec, out int[]? palette, out bool compressed, out int bitPlaneOffset)) return false;
        if (palette == null || palette.Length == 0) return false;

        // A palette of one entry is the whole layer (the game treats count <= 1 as empty).
        if (palette.Length == 1)
        {
            blockId = palette[0];
            return true;
        }

        // The bit planes are always a zstd frame running to the end of the blob, even when
        // the palette itself is stored raw; decompressed it is bitsize * 4096 bytes
        // (32768 blocks * bitsize bits / 8), where bitsize is the game's floor(log2(count)).
        if (codec == null) return false;
        int frameLength = blob!.Length - bitPlaneOffset;
        if (frameLength <= 0) return false;

        byte[] planes = codec.Decompress(blob, bitPlaneOffset, frameLength);
        if (planes == null || planes.Length == 0 || planes.Length % 4096 != 0) return false;
        int bitsize = planes.Length / 4096;

        int wordIndex = index >> 5, bit = index & 31;
        int value = 0;
        for (int plane = 0; plane < bitsize; plane++)
        {
            int word = BinaryPrimitives.ReadInt32LittleEndian(planes.AsSpan(plane * 4096 + wordIndex * 4, 4));
            value |= ((word >> bit) & 1) << plane;
        }

        if (value < 0 || value >= palette.Length) return false;
        blockId = palette[value];
        return true;
    }

    public static bool TryReadPalette(
        byte[]? blob,
        IZstdCodec? codec,
        out int[]? palette,
        out bool compressed,
        out int bitPlaneOffset)
    {
        palette = null;
        compressed = false;
        bitPlaneOffset = 0;

        if (blob == null || blob.Length < HeaderSize) return false;

        int n = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(0, 4));
        if (n == 0) return false; // empty layer

        if (n < 0)
        {
            // -int.MinValue overflows, and the length must not be added as an int —
            // otherwise a corrupt blob passes the bounds check.
            if (n == int.MinValue) return false;
            int length = -n;
            if (length % 4 != 0 || (long)HeaderSize + length > blob.Length) return false;
            int count = length / 4;
            var values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(HeaderSize + i * 4, 4));
            palette = values;
            compressed = false;
            bitPlaneOffset = HeaderSize + length;
            return true;
        }

        if ((long)HeaderSize + n > blob.Length) return false;
        if (codec == null) return false;

        byte[] raw = codec.Decompress(blob, HeaderSize, n);
        if (raw == null || raw.Length == 0 || raw.Length % 4 != 0) return false;

        int entries = raw.Length / 4;
        var result = new int[entries];
        for (int i = 0; i < entries; i++)
            result[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4, 4));
        palette = result;
        compressed = true;
        bitPlaneOffset = HeaderSize + n;
        return true;
    }
}
