using Vintagestory.Common;
using VsChunkDump.Core;

namespace VsFullCapture;

/// <summary>
/// Zstd from the game itself. Palettes in blobs are compressed with the same
/// zstd at the same level (-3) and with a recorded frame size, so a third-party
/// zstd does not fit here: <c>ZStdWrapper.GetDecompressedSize</c> requires the
/// size in the frame header, and the game's compressor writes it.
///
/// One instance per thread: <see cref="CompressionZSTD"/> marks its buffers as
/// [ThreadStatic], but the object itself is cheaper to keep close to where it is used.
/// </summary>
public sealed class GameZstdCodec : IZstdCodec
{
    private readonly CompressionZSTD _zstd = new();

    public byte[] Decompress(byte[] data, int offset, int length) => _zstd.Decompress(data, offset, length);

    public byte[] Compress(byte[] data, int length) => _zstd.Compress(data, length, 0);
}
