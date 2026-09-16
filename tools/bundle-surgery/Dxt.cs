// Dxt — reading DXT1/DXT5 (BC1/BC3) blocks.
//
// Two jobs. The report needs to know whether a DXT1 payload uses the
// format's one-bit transparency, because that decides whether a same-size
// ETC2 swap has to target ETC2_RGBA1 rather than ETC2_RGB: a DXT1 block whose
// first colour sorts at or below its second is in "three-colour" mode, and any
// texel in that block with index 3 is transparent black. Convert such a
// payload to opaque ETC2_RGB and every cut-out sprite grows a black halo.
//
// The decoders are the other half of a later conversion (decode DXT, encode
// ETC2), written now so the tests that pin their arithmetic exist before any
// texture is touched. They are deliberately plain: correctness over speed,
// and every rounding matches the D3D reference so a decode-encode round trip
// can be compared against a known image.

namespace BundleSurgery;

internal static class Dxt
{
    public const int Dxt1BlockBytes = 8;
    public const int Dxt5BlockBytes = 16;

    /// <summary>
    /// True when this 8-byte DXT1 colour block is in three-colour mode and
    /// uses index 3, i.e. holds at least one transparent texel.
    /// </summary>
    public static bool Dxt1BlockUsesPunchThrough(ReadOnlySpan<byte> block)
    {
        if (block.Length < Dxt1BlockBytes) throw new ArgumentException("DXT1 block is 8 bytes", nameof(block));
        ushort c0 = (ushort)(block[0] | (block[1] << 8));
        ushort c1 = (ushort)(block[2] | (block[3] << 8));
        if (c0 > c1) return false;
        uint indices = (uint)(block[4] | (block[5] << 8) | (block[6] << 16) | (block[7] << 24));
        for (int i = 0; i < 16; i++)
            if (((indices >> (2 * i)) & 3) == 3) return true;
        return false;
    }

    /// <summary>
    /// How many blocks of a DXT1 payload (one level or a whole chain -- the
    /// blocks are self-describing) contain transparent texels.
    /// </summary>
    public static int CountDxt1PunchThroughBlocks(ReadOnlySpan<byte> payload)
    {
        if (payload.Length % Dxt1BlockBytes != 0)
            throw new InvalidDataException($"DXT1 payload of {payload.Length} bytes is not whole 8-byte blocks");
        int count = 0;
        for (int offset = 0; offset < payload.Length; offset += Dxt1BlockBytes)
            if (Dxt1BlockUsesPunchThrough(payload.Slice(offset, Dxt1BlockBytes))) count++;
        return count;
    }

    static void Rgb565(ushort c, out byte r, out byte g, out byte b)
    {
        int r5 = (c >> 11) & 31, g6 = (c >> 5) & 63, b5 = c & 31;
        r = (byte)((r5 << 3) | (r5 >> 2));
        g = (byte)((g6 << 2) | (g6 >> 4));
        b = (byte)((b5 << 3) | (b5 >> 2));
    }

    /// <summary>
    /// Decodes one DXT1 colour block into 16 RGBA texels (64 bytes, row major).
    /// <paramref name="fourColour"/> forces four-colour mode, which is how the
    /// colour half of a DXT5 block is always read.
    /// </summary>
    public static void DecodeColourBlock(ReadOnlySpan<byte> block, Span<byte> rgba, bool fourColour)
    {
        if (block.Length < Dxt1BlockBytes) throw new ArgumentException("DXT1 block is 8 bytes", nameof(block));
        if (rgba.Length < 64) throw new ArgumentException("16 RGBA texels are 64 bytes", nameof(rgba));
        ushort c0 = (ushort)(block[0] | (block[1] << 8));
        ushort c1 = (ushort)(block[2] | (block[3] << 8));
        Rgb565(c0, out byte r0, out byte g0, out byte b0);
        Rgb565(c1, out byte r1, out byte g1, out byte b1);

        Span<byte> palette = stackalloc byte[16];
        palette[0] = r0; palette[1] = g0; palette[2] = b0; palette[3] = 255;
        palette[4] = r1; palette[5] = g1; palette[6] = b1; palette[7] = 255;
        if (fourColour || c0 > c1)
        {
            palette[8] = (byte)((2 * r0 + r1 + 1) / 3);
            palette[9] = (byte)((2 * g0 + g1 + 1) / 3);
            palette[10] = (byte)((2 * b0 + b1 + 1) / 3);
            palette[11] = 255;
            palette[12] = (byte)((r0 + 2 * r1 + 1) / 3);
            palette[13] = (byte)((g0 + 2 * g1 + 1) / 3);
            palette[14] = (byte)((b0 + 2 * b1 + 1) / 3);
            palette[15] = 255;
        }
        else
        {
            palette[8] = (byte)((r0 + r1) / 2);
            palette[9] = (byte)((g0 + g1) / 2);
            palette[10] = (byte)((b0 + b1) / 2);
            palette[11] = 255;
            // Transparent black: the punch-through texel.
            palette[12] = 0; palette[13] = 0; palette[14] = 0; palette[15] = 0;
        }

        uint indices = (uint)(block[4] | (block[5] << 8) | (block[6] << 16) | (block[7] << 24));
        for (int i = 0; i < 16; i++)
        {
            int p = (int)((indices >> (2 * i)) & 3) * 4;
            rgba[i * 4] = palette[p];
            rgba[i * 4 + 1] = palette[p + 1];
            rgba[i * 4 + 2] = palette[p + 2];
            rgba[i * 4 + 3] = palette[p + 3];
        }
    }

    /// <summary>Decodes one 16-byte DXT5 block (8 bytes of alpha, 8 of colour) into 16 RGBA texels.</summary>
    public static void DecodeDxt5Block(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        if (block.Length < Dxt5BlockBytes) throw new ArgumentException("DXT5 block is 16 bytes", nameof(block));
        DecodeColourBlock(block.Slice(8, 8), rgba, fourColour: true);

        byte a0 = block[0], a1 = block[1];
        Span<byte> alpha = stackalloc byte[8];
        alpha[0] = a0;
        alpha[1] = a1;
        if (a0 > a1)
        {
            for (int i = 1; i <= 6; i++) alpha[i + 1] = (byte)(((7 - i) * a0 + i * a1 + 3) / 7);
        }
        else
        {
            for (int i = 1; i <= 4; i++) alpha[i + 1] = (byte)(((5 - i) * a0 + i * a1 + 2) / 5);
            alpha[6] = 0;
            alpha[7] = 255;
        }

        ulong indices = 0;
        for (int i = 0; i < 6; i++) indices |= (ulong)block[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
            rgba[i * 4 + 3] = alpha[(int)((indices >> (3 * i)) & 7)];
    }

    /// <summary>
    /// Decodes one mip level of a DXT1 or DXT5 payload to RGBA32, width*height*4
    /// bytes. Partial edge blocks are clipped, which is what the GPU does too.
    /// </summary>
    public static byte[] DecodeLevel(TextureFormatInfo format, ReadOnlySpan<byte> data, int width, int height)
    {
        bool dxt5 = format.Family == TextureFamily.Dxt5;
        if (!dxt5 && format.Family != TextureFamily.Dxt1)
            throw new NotSupportedException($"{format.Name} is not a DXT1 or DXT5 payload");
        int blockBytes = dxt5 ? Dxt5BlockBytes : Dxt1BlockBytes;
        int blocksWide = (width + 3) / 4, blocksHigh = (height + 3) / 4;
        long expected = (long)blocksWide * blocksHigh * blockBytes;
        if (data.Length != expected)
            throw new InvalidDataException(
                $"{format.Name} level {width}x{height} needs {expected} bytes, payload has {data.Length}");

        var rgba = new byte[width * height * 4];
        Span<byte> texels = stackalloc byte[64];
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                var block = data.Slice((by * blocksWide + bx) * blockBytes, blockBytes);
                if (dxt5) DecodeDxt5Block(block, texels);
                else DecodeColourBlock(block, texels, fourColour: false);
                for (int ty = 0; ty < 4; ty++)
                {
                    int y = by * 4 + ty;
                    if (y >= height) break;
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int x = bx * 4 + tx;
                        if (x >= width) break;
                        int src = (ty * 4 + tx) * 4, dst = (y * width + x) * 4;
                        rgba[dst] = texels[src];
                        rgba[dst + 1] = texels[src + 1];
                        rgba[dst + 2] = texels[src + 2];
                        rgba[dst + 3] = texels[src + 3];
                    }
                }
            }
        }
        return rgba;
    }
}
