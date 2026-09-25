using System.Buffers.Binary;
using System.IO.Compression;

namespace VsChunkDump.Core;

/// <summary>
/// Minimal PNG encoder (RGBA8, no interlacing). Built on the in-box
/// <see cref="ZLibStream"/> so that no external dependencies are needed — the tool
/// must build offline.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void WriteRgba(string path, int width, int height, byte[] rgba)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, EncodeRgba(width, height, rgba));
    }

    public static byte[] EncodeRgba(int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} RGBA bytes, got {rgba.Length}");

        // Scanlines with filter 0 (None).
        var raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (1 + width * 4);
            raw[dst] = 0;
            Buffer.BlockCopy(rgba, y * width * 4, raw, dst + 1, width * 4);
        }

        byte[] compressed;
        using (var ms = new MemoryStream(raw.Length / 4 + 64))
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw);
            }
            compressed = ms.ToArray();
        }

        using var outMs = new MemoryStream(compressed.Length + 128);
        outMs.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: truecolor + alpha
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(outMs, "IHDR", ihdr);
        WriteChunk(outMs, "IDAT", compressed);
        WriteChunk(outMs, "IEND", ReadOnlySpan<byte>.Empty);

        return outMs.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        s.Write(typeBytes);
        s.Write(data);

        uint crc = Crc32.Compute(typeBytes);
        crc = Crc32.Compute(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }
}
