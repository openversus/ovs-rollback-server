// CompressionHelper.cs
namespace Rollback.Core;

public static class CompressionHelper
{
    private const int MaxBuffer = 1024;

    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty) return [];

        var outBuf = new byte[MaxBuffer];
        int inPos = 0, outPos = 0;

        while (inPos < input.Length)
        {
            if (outPos >= MaxBuffer)
                throw new InvalidOperationException("compressPacket: output overflow (1024 bytes)");

            int maskPos = outPos++;
            byte mask = 0;

            for (byte bit = 0; bit < 8 && inPos < input.Length; bit++, inPos++)
            {
                byte v = input[inPos];
                if (v != 0)
                {
                    mask |= (byte)(1 << bit);
                    if (outPos >= MaxBuffer)
                        throw new InvalidOperationException("compressPacket: output overflow (1024 bytes)");
                    outBuf[outPos++] = v;
                }
            }

            outBuf[maskPos] = mask;
        }

        return outBuf[..outPos];
    }

    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int originalLength = MaxBuffer)
    {
        if (originalLength > MaxBuffer)
            throw new ArgumentOutOfRangeException(nameof(originalLength));

        var outBuf = new byte[originalLength];
        int readPos = 0, writePos = 0;

        while (readPos < compressed.Length && writePos < originalLength)
        {
            byte mask = compressed[readPos++];

            for (byte bit = 0; bit < 8 && writePos < originalLength; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                {
                    if (readPos >= compressed.Length)
                        throw new InvalidOperationException("decompressPacket: truncated data");
                    outBuf[writePos++] = compressed[readPos++];
                }
                else
                {
                    outBuf[writePos++] = 0;
                }
            }
        }

        return outBuf;
    }
}

