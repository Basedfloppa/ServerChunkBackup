using System.Buffers.Binary;

namespace VsChunkDump.Core;

/// <summary>
/// Packing a block layer into 'palette + bit stream' — the same idea the game
/// itself uses (ChunkDataLayer: int[] palette + int[][] dataBits).
///
/// Layer format in the dump:
///   u8   bits                 — bits per index (0 = the layer is uniform)
///   u16  paletteCount         — number of palette entries (1..32768)
///   i32[paletteCount]         — block ids (LE)
///   u8[ceil(32768*bits/8)]    — packed indices (LSB-first)
/// </summary>
public sealed class ChunkLayer
{
    /// <summary>Palette values (block ids).</summary>
    public int[] Palette = [];

    /// <summary>Bits per index. 0 if the layer holds exactly one value.</summary>
    public byte Bits;

    /// <summary>Packed indices.</summary>
    public byte[] Packed = [];

    /// <summary>How many bits are needed to encode <paramref name="count"/> variants.</summary>
    public static int BitsFor(int count)
    {
        if (count <= 1) return 0;
        int bits = 0;
        int v = count - 1;
        while (v > 0) { bits++; v >>= 1; }
        return bits;
    }

    /// <summary>Pack an array of values (length <see cref="ChunkGeometry.BlocksPerChunk"/>).</summary>
    public static ChunkLayer Encode(ReadOnlySpan<int> values)
    {
        var map = new Dictionary<int, int>(capacity: 64);
        var palette = new List<int>(64);
        int n = values.Length;
        var indices = new int[n];

        for (int i = 0; i < n; i++)
        {
            int v = values[i];
            if (!map.TryGetValue(v, out int idx))
            {
                idx = palette.Count;
                palette.Add(v);
                map[v] = idx;
            }
            indices[i] = idx;
        }

        int bits = BitsFor(palette.Count);
        if (bits == 0)
        {
            return new ChunkLayer { Palette = palette.ToArray(), Bits = 0, Packed = [] };
        }

        var packed = new byte[(n * bits + 7) / 8];
        ulong buf = 0;
        int bufBits = 0;
        int o = 0;
        for (int i = 0; i < n; i++)
        {
            buf |= (ulong)(uint)indices[i] << bufBits;
            bufBits += bits;
            while (bufBits >= 8)
            {
                packed[o++] = (byte)(buf & 0xFF);
                buf >>= 8;
                bufBits -= 8;
            }
        }
        if (bufBits > 0) packed[o] = (byte)(buf & 0xFF);

        return new ChunkLayer { Palette = palette.ToArray(), Bits = (byte)bits, Packed = packed };
    }

    /// <summary>Unpack the layer into <paramref name="dest"/> (length <see cref="ChunkGeometry.BlocksPerChunk"/>).</summary>
    public void Decode(Span<int> dest)
    {
        if (Palette.Length == 0)
        {
            dest.Fill(0);
            return;
        }
        if (Bits == 0)
        {
            dest.Fill(Palette[0]);
            return;
        }

        int bits = Bits;
        int mask = (1 << bits) - 1;
        ulong buf = 0;
        int bufBits = 0;
        int pos = 0;
        var packed = Packed;

        for (int i = 0; i < dest.Length; i++)
        {
            while (bufBits < bits)
            {
                buf |= pos < packed.Length ? (ulong)packed[pos++] << bufBits : 0UL;
                bufBits += 8;
            }
            int idx = (int)(buf & (ulong)mask);
            buf >>= bits;
            bufBits -= bits;
            dest[i] = idx < Palette.Length ? Palette[idx] : 0;
        }
    }

    /// <summary>How many bytes the layer occupies uncompressed.</summary>
    public int EncodedSize => 3 + Palette.Length * 4 + Packed.Length;

    public void WriteTo(Stream s)
    {
        s.WriteByte(Bits);
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, (ushort)Palette.Length);
        s.Write(tmp);
        Span<byte> id = stackalloc byte[4];
        foreach (int p in Palette)
        {
            BinaryPrimitives.WriteInt32LittleEndian(id, p);
            s.Write(id);
        }
        s.Write(Packed);
    }

    public static ChunkLayer ReadFrom(Stream s)
    {
        var layer = new ChunkLayer();
        int bits = s.ReadByte();
        if (bits < 0) throw new EndOfStreamException("ChunkLayer: unexpected end of stream (bits)");
        layer.Bits = (byte)bits;

        Span<byte> tmp = stackalloc byte[2];
        ReadExactly(s, tmp);
        int paletteCount = BinaryPrimitives.ReadUInt16LittleEndian(tmp);
        var palette = new int[paletteCount];
        Span<byte> id = stackalloc byte[4];
        for (int i = 0; i < paletteCount; i++)
        {
            ReadExactly(s, id);
            palette[i] = BinaryPrimitives.ReadInt32LittleEndian(id);
        }
        layer.Palette = palette;

        int packedLen = (ChunkGeometry.BlocksPerChunk * layer.Bits + 7) / 8;
        var packed = new byte[packedLen];
        ReadExactly(s, packed);
        layer.Packed = packed;

        return layer;
    }

    internal static void ReadExactly(Stream s, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer[read..]);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of stream");
            read += n;
        }
    }
}
