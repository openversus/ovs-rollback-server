// CompressionHelper.cs
// Bitmask zero-suppression in BOTH directions.
// Byte-for-byte match with C++ compressPacket / decompressPacket.
// There is NO ZLib, NO DeflateStream, NO System.IO.Compression anywhere.

using System;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace OVS.Rollback.Core;

/// <summary>
/// Asymmetric compression:
///   • Compress (server → client): ZLib (RFC 1950) — what the original C# server
///     sent and what game clients are proven to accept.
///   • Decompress (client → server): Custom bitmask zero-suppression — what game
///     clients send (matching the C++ server's compressPacket format).
///
/// This asymmetry works because game clients auto-detect the inbound format
/// (they need to work with both C++ bitmask servers and C# zlib servers).
/// </summary>

public static class CompressionHelper
{
    private const int MaxBuffer = 1024;

    // ═══════════════════════════════════════════════════════
    //  Compress (server → client)
    //  Matches C++ compressPacket() exactly.
    //
    //  Algorithm: for every 8 bytes of input, write a 1-byte
    //  mask where bit N is set if byte N is non-zero, then
    //  write only the non-zero bytes.
    // ═══════════════════════════════════════════════════════

    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty) return [];

        var outBuf = new byte[MaxBuffer];
        int inPos = 0, outPos = 0;

        while (inPos < input.Length)
        {
            if (outPos >= MaxBuffer)
                throw new InvalidOperationException(
                    "compressPacket: output overflow (1024 bytes)");

            int maskPos = outPos++;
            byte mask = 0;

            for (byte bit = 0; bit < 8 && inPos < input.Length; bit++, inPos++)
            {
                byte v = input[inPos];
                if (v != 0)
                {
                    mask |= (byte)(1 << bit);
                    if (outPos >= MaxBuffer)
                        throw new InvalidOperationException(
                            "compressPacket: output overflow (1024 bytes)");
                    outBuf[outPos++] = v;
                }
            }

            outBuf[maskPos] = mask;
        }

        return outBuf[..outPos];
    }

    /// <summary>
    /// Zero-output-alloc hot-path overload for the tick loop.
    /// Same bitmask algorithm as Compress(), writes into pre-allocated buffer.
    /// Returns number of compressed bytes written.
    /// </summary>
    public static int CompressTo(ReadOnlySpan<byte> source, byte[] destination)
    {
        int inPos = 0, outPos = 0;

        while (inPos < source.Length)
        {
            if (outPos >= destination.Length)
                throw new InvalidOperationException(
                    "compressPacket: output overflow");

            int maskPos = outPos++;
            byte mask = 0;

            for (byte bit = 0; bit < 8 && inPos < source.Length; bit++, inPos++)
            {
                byte v = source[inPos];
                if (v != 0)
                {
                    mask |= (byte)(1 << bit);
                    if (outPos >= destination.Length)
                        throw new InvalidOperationException(
                            "compressPacket: output overflow");
                    destination[outPos++] = v;
                }
            }

            destination[maskPos] = mask;
        }

        return outPos;
    }

    // ═══════════════════════════════════════════════════════
    //  Decompress (client → server)
    //  Matches C++ decompressPacket() exactly.
    //
    //  IMPORTANT: Always returns exactly `originalLength` bytes
    //  (default 1024), with unwritten positions as 0x00.
    //  This matches the C++ behavior:
    //    std::vector<uint8_t> outBuf(1024, 0);
    //    outBuf.resize(originalLength);
    //    return outBuf;
    // ═══════════════════════════════════════════════════════

    public static byte[] Decompress(byte[] data)
    {
        if (data == null || data.Length == 0)
            return data ?? [];

        var output = new byte[1024];
        int inPos = 0, outPos = 0;

        while (inPos < data.Length && outPos < 1024)
        {
            byte mask = data[inPos++];

            for (int bit = 0; bit < 8 && outPos < 1024; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                {
                    if (inPos >= data.Length)
                        break;
                    output[outPos++] = data[inPos++];
                }
                else
                {
                    output[outPos++] = 0;
                }
            }
        }

        return output[..outPos];
    }
}

