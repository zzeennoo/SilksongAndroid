// Bc7 — a BC7 (BPTC) decoder, written from the specification.
//
// Why: the texture report showed the depot's atlases are BC7, not DXT --
// 940 textures, 3.26 GiB on disk, which the Android player expands to
// 13 GiB of RGBA32 because no Mali or Adreno samples BC7. BC7 is 16 bytes
// per 4x4 block, exactly ETC2_RGBA8's size, so the same-size swap applies
// unchanged; the only new piece is reading BC7 blocks.
//
// BC7 has eight modes, selected by the position of the first set bit. Each
// mode fixes how many subsets (1-3) share the block, how many bits each
// colour and alpha endpoint has, whether a per-endpoint or per-subset
// "p-bit" extends them, and how wide the interpolation indices are. Modes
// 4 and 5 add a channel rotation and separate alpha indices. Everything
// here follows the Khronos BPTC description (KHR_texture_compression_bptc)
// and the D3D11 functional specification, which agree bit for bit.
//
// Bits are numbered from the least significant bit of byte 0 -- the
// opposite of ETC2, whose words are big-endian.
//
// The decoder is verified two ways: tests with hand-built blocks whose
// output is known, and a random-block comparison against an independent
// decoder (texture2ddecoder) run outside CI, since a mistake in one of the
// partition or anchor tables shows up only for particular blocks.

namespace BundleSurgery;

internal static class Bc7
{
    public const int BlockBytes = 16;

    // Per mode: subsets, partition bits, rotation bits, index-selection bit,
    // colour bits, alpha bits, per-endpoint p-bits, shared p-bits, index
    // bits, second index bits.
    static readonly int[,] Modes =
    {
        // ns  pb  rb  isb  cb  ab  epb  spb  ib  ib2
        {  3,  4,  0,  0,   4,  0,  1,   0,   3,  0 },
        {  2,  6,  0,  0,   6,  0,  0,   1,   3,  0 },
        {  3,  6,  0,  0,   5,  0,  0,   0,   2,  0 },
        {  2,  6,  0,  0,   7,  0,  1,   0,   2,  0 },
        {  1,  0,  2,  1,   5,  6,  0,   0,   2,  3 },
        {  1,  0,  2,  0,   7,  8,  0,   0,   2,  2 },
        {  1,  0,  0,  0,   7,  7,  1,   0,   4,  0 },
        {  2,  6,  0,  0,   5,  5,  1,   0,   2,  0 },
    };

    static readonly int[] Weights2 = { 0, 21, 43, 64 };
    static readonly int[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    static readonly int[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    static readonly byte[,] Partitions2 =
    {
        {0,0,1,1, 0,0,1,1, 0,0,1,1, 0,0,1,1}, {0,0,0,1, 0,0,0,1, 0,0,0,1, 0,0,0,1},
        {0,1,1,1, 0,1,1,1, 0,1,1,1, 0,1,1,1}, {0,0,0,1, 0,0,1,1, 0,0,1,1, 0,1,1,1},
        {0,0,0,0, 0,0,0,1, 0,0,0,1, 0,0,1,1}, {0,0,1,1, 0,1,1,1, 0,1,1,1, 1,1,1,1},
        {0,0,0,1, 0,0,1,1, 0,1,1,1, 1,1,1,1}, {0,0,0,0, 0,0,0,1, 0,0,1,1, 0,1,1,1},
        {0,0,0,0, 0,0,0,0, 0,0,0,1, 0,0,1,1}, {0,0,1,1, 0,1,1,1, 1,1,1,1, 1,1,1,1},
        {0,0,0,0, 0,0,0,1, 0,1,1,1, 1,1,1,1}, {0,0,0,0, 0,0,0,0, 0,0,0,1, 0,1,1,1},
        {0,0,0,1, 0,1,1,1, 1,1,1,1, 1,1,1,1}, {0,0,0,0, 0,0,0,0, 1,1,1,1, 1,1,1,1},
        {0,0,0,0, 1,1,1,1, 1,1,1,1, 1,1,1,1}, {0,0,0,0, 0,0,0,0, 0,0,0,0, 1,1,1,1},
        {0,0,0,0, 1,0,0,0, 1,1,1,0, 1,1,1,1}, {0,1,1,1, 0,0,0,1, 0,0,0,0, 0,0,0,0},
        {0,0,0,0, 0,0,0,0, 1,0,0,0, 1,1,1,0}, {0,1,1,1, 0,0,1,1, 0,0,0,1, 0,0,0,0},
        {0,0,1,1, 0,0,0,1, 0,0,0,0, 0,0,0,0}, {0,0,0,0, 1,0,0,0, 1,1,0,0, 1,1,1,0},
        {0,0,0,0, 0,0,0,0, 1,0,0,0, 1,1,0,0}, {0,1,1,1, 0,0,1,1, 0,0,1,1, 0,0,0,1},
        {0,0,1,1, 0,0,0,1, 0,0,0,1, 0,0,0,0}, {0,0,0,0, 1,0,0,0, 1,0,0,0, 1,1,0,0},
        {0,1,1,0, 0,1,1,0, 0,1,1,0, 0,1,1,0}, {0,0,1,1, 0,1,1,0, 0,1,1,0, 1,1,0,0},
        {0,0,0,1, 0,1,1,1, 1,1,1,0, 1,0,0,0}, {0,0,0,0, 1,1,1,1, 1,1,1,1, 0,0,0,0},
        {0,1,1,1, 0,0,0,1, 1,0,0,0, 1,1,1,0}, {0,0,1,1, 1,0,0,1, 1,0,0,1, 1,1,0,0},
        {0,1,0,1, 0,1,0,1, 0,1,0,1, 0,1,0,1}, {0,0,0,0, 1,1,1,1, 0,0,0,0, 1,1,1,1},
        {0,1,0,1, 1,0,1,0, 0,1,0,1, 1,0,1,0}, {0,0,1,1, 0,0,1,1, 1,1,0,0, 1,1,0,0},
        {0,0,1,1, 1,1,0,0, 0,0,1,1, 1,1,0,0}, {0,1,0,1, 0,1,0,1, 1,0,1,0, 1,0,1,0},
        {0,1,1,0, 1,0,0,1, 0,1,1,0, 1,0,0,1}, {0,1,0,1, 1,0,1,0, 1,0,1,0, 0,1,0,1},
        {0,1,1,1, 0,0,1,1, 1,1,0,0, 1,1,1,0}, {0,0,0,1, 0,0,1,1, 1,1,0,0, 1,0,0,0},
        {0,0,1,1, 0,0,1,0, 0,1,0,0, 1,1,0,0}, {0,0,1,1, 1,0,1,1, 1,1,0,1, 1,1,0,0},
        {0,1,1,0, 1,0,0,1, 1,0,0,1, 0,1,1,0}, {0,0,1,1, 1,1,0,0, 1,1,0,0, 0,0,1,1},
        {0,1,1,0, 0,1,1,0, 1,0,0,1, 1,0,0,1}, {0,0,0,0, 0,1,1,0, 0,1,1,0, 0,0,0,0},
        {0,1,0,0, 1,1,1,0, 0,1,0,0, 0,0,0,0}, {0,0,1,0, 0,1,1,1, 0,0,1,0, 0,0,0,0},
        {0,0,0,0, 0,0,1,0, 0,1,1,1, 0,0,1,0}, {0,0,0,0, 0,1,0,0, 1,1,1,0, 0,1,0,0},
        {0,1,1,0, 1,1,0,0, 1,0,0,1, 0,0,1,1}, {0,0,1,1, 0,1,1,0, 1,1,0,0, 1,0,0,1},
        {0,1,1,0, 0,0,1,1, 1,0,0,1, 1,1,0,0}, {0,0,1,1, 1,0,0,1, 1,1,0,0, 0,1,1,0},
        {0,1,1,0, 1,1,0,0, 1,1,0,0, 1,0,0,1}, {0,1,1,0, 0,0,1,1, 0,0,1,1, 1,0,0,1},
        {0,1,1,1, 1,1,1,0, 1,0,0,0, 0,0,0,1}, {0,0,0,1, 1,0,0,0, 1,1,1,0, 0,1,1,1},
        {0,0,0,0, 1,1,1,1, 0,0,1,1, 0,0,1,1}, {0,0,1,1, 0,0,1,1, 1,1,1,1, 0,0,0,0},
        {0,0,1,0, 0,0,1,0, 1,1,1,0, 1,1,1,0}, {0,1,0,0, 0,1,0,0, 0,1,1,1, 0,1,1,1},
    };

    static readonly byte[,] Partitions3 =
    {
        {0,0,1,1, 0,0,1,1, 0,2,2,1, 2,2,2,2}, {0,0,0,1, 0,0,1,1, 2,2,1,1, 2,2,2,1},
        {0,0,0,0, 2,0,0,1, 2,2,1,1, 2,2,1,1}, {0,2,2,2, 0,0,2,2, 0,0,1,1, 0,1,1,1},
        {0,0,0,0, 0,0,0,0, 1,1,2,2, 1,1,2,2}, {0,0,1,1, 0,0,1,1, 0,0,2,2, 0,0,2,2},
        {0,0,2,2, 0,0,2,2, 1,1,1,1, 1,1,1,1}, {0,0,1,1, 0,0,1,1, 2,2,1,1, 2,2,1,1},
        {0,0,0,0, 0,0,0,0, 1,1,1,1, 2,2,2,2}, {0,0,0,0, 1,1,1,1, 1,1,1,1, 2,2,2,2},
        {0,0,0,0, 1,1,1,1, 2,2,2,2, 2,2,2,2}, {0,0,1,2, 0,0,1,2, 0,0,1,2, 0,0,1,2},
        {0,1,1,2, 0,1,1,2, 0,1,1,2, 0,1,1,2}, {0,1,2,2, 0,1,2,2, 0,1,2,2, 0,1,2,2},
        {0,0,1,1, 0,1,1,2, 1,1,2,2, 1,2,2,2}, {0,0,1,1, 2,0,0,1, 2,2,0,0, 2,2,2,0},
        {0,0,0,1, 0,0,1,1, 0,1,1,2, 1,1,2,2}, {0,1,1,1, 0,0,1,1, 2,0,0,1, 2,2,0,0},
        {0,0,0,0, 1,1,2,2, 1,1,2,2, 1,1,2,2}, {0,0,2,2, 0,0,2,2, 0,0,2,2, 1,1,1,1},
        {0,1,1,1, 0,1,1,1, 0,2,2,2, 0,2,2,2}, {0,0,0,1, 0,0,0,1, 2,2,2,1, 2,2,2,1},
        {0,0,0,0, 0,0,1,1, 0,1,2,2, 0,1,2,2}, {0,0,0,0, 1,1,0,0, 2,2,1,0, 2,2,1,0},
        {0,1,2,2, 0,1,2,2, 0,0,1,1, 0,0,0,0}, {0,0,1,2, 0,0,1,2, 1,1,2,2, 2,2,2,2},
        {0,1,1,0, 1,2,2,1, 1,2,2,1, 0,1,1,0}, {0,0,0,0, 0,1,1,0, 1,2,2,1, 1,2,2,1},
        {0,0,2,2, 1,1,0,2, 1,1,0,2, 0,0,2,2}, {0,1,1,0, 0,1,1,0, 2,0,0,2, 2,2,2,2},
        {0,0,1,1, 0,1,2,2, 0,1,2,2, 0,0,1,1}, {0,0,0,0, 2,0,0,0, 2,2,1,1, 2,2,2,1},
        {0,0,0,0, 0,0,0,2, 1,1,2,2, 1,2,2,2}, {0,2,2,2, 0,0,2,2, 0,0,1,2, 0,0,1,1},
        {0,0,1,1, 0,0,1,2, 0,0,2,2, 0,2,2,2}, {0,1,2,0, 0,1,2,0, 0,1,2,0, 0,1,2,0},
        {0,0,0,0, 1,1,1,1, 2,2,2,2, 0,0,0,0}, {0,1,2,0, 1,2,0,1, 2,0,1,2, 0,1,2,0},
        {0,1,2,0, 2,0,1,2, 1,2,0,1, 0,1,2,0}, {0,0,1,1, 2,2,0,0, 1,1,2,2, 0,0,1,1},
        {0,0,1,1, 1,1,2,2, 2,2,0,0, 0,0,1,1}, {0,1,0,1, 0,1,0,1, 2,2,2,2, 2,2,2,2},
        {0,0,0,0, 0,0,0,0, 2,1,2,1, 2,1,2,1}, {0,0,2,2, 1,1,2,2, 0,0,2,2, 1,1,2,2},
        {0,0,2,2, 0,0,1,1, 0,0,2,2, 0,0,1,1}, {0,2,2,0, 1,2,2,1, 0,2,2,0, 1,2,2,1},
        {0,1,0,1, 2,2,2,2, 2,2,2,2, 0,1,0,1}, {0,0,0,0, 2,1,2,1, 2,1,2,1, 2,1,2,1},
        {0,1,0,1, 0,1,0,1, 0,1,0,1, 2,2,2,2}, {0,2,2,2, 0,1,1,1, 0,2,2,2, 0,1,1,1},
        {0,0,0,2, 1,1,1,2, 0,0,0,2, 1,1,1,2}, {0,0,0,0, 2,1,1,2, 2,1,1,2, 2,1,1,2},
        {0,2,2,2, 0,1,1,1, 0,1,1,1, 0,2,2,2}, {0,0,0,2, 1,1,1,2, 1,1,1,2, 0,0,0,2},
        {0,1,1,0, 0,1,1,0, 0,1,1,0, 2,2,2,2}, {0,0,0,0, 0,0,0,0, 2,1,1,2, 2,1,1,2},
        {0,1,1,0, 0,1,1,0, 2,2,2,2, 2,2,2,2}, {0,0,2,2, 0,0,1,1, 0,0,1,1, 0,0,2,2},
        {0,0,2,2, 1,1,2,2, 1,1,2,2, 0,0,2,2}, {0,0,0,0, 0,0,0,0, 0,0,0,0, 2,1,1,2},
        {0,0,0,2, 0,0,0,1, 0,0,0,2, 0,0,0,1}, {0,2,2,2, 1,2,2,2, 0,2,2,2, 1,2,2,2},
        {0,1,0,1, 2,2,2,2, 2,2,2,2, 2,2,2,2}, {0,1,1,1, 2,0,1,1, 2,2,0,1, 2,2,2,0},
    };

    // The pixel whose index is one bit short, per partition: the first
    // pixel of subset 1 (two subsets), and of subsets 1 and 2 (three).
    static readonly byte[] AnchorSecondOfTwo =
    {
        15,15,15,15,15,15,15,15, 15,15,15,15,15,15,15,15,
        15, 2, 8, 2, 2, 8, 8,15,  2, 8, 2, 2, 8, 8, 2, 2,
        15,15, 6, 8, 2, 8,15,15,  2, 8, 2, 2, 2,15,15, 6,
         6, 2, 6, 8,15,15, 2, 2, 15,15,15,15,15, 2, 2,15,
    };
    static readonly byte[] AnchorSecondOfThree =
    {
         3, 3,15,15, 8, 3,15,15,  8, 8, 6, 6, 6, 5, 3, 3,
         3, 3, 8,15, 3, 3, 6,10,  5, 8, 8, 6, 8, 5,15,15,
         8,15, 3, 5, 6,10, 8,15, 15, 3,15, 5,15,15,15,15,
         3,15, 5, 5, 5, 8, 5,10,  5,10, 8,13,15,12, 3, 3,
    };
    static readonly byte[] AnchorThirdOfThree =
    {
        15, 8, 8, 3,15,15, 3, 8, 15,15,15,15,15,15,15, 8,
        15, 8,15, 3,15, 8,15, 8,  3,15, 6,10,15,15,10, 8,
        15, 3,15,10,10, 8, 9,10,  6,15, 8,15, 3, 6, 6, 8,
        15, 3,15,15,15,15,15,15, 15,15,15,15, 3,15,15, 8,
    };

    ref struct BitReader
    {
        readonly ReadOnlySpan<byte> _block;
        int _position;
        public BitReader(ReadOnlySpan<byte> block) { _block = block; _position = 0; }
        public int Position => _position;

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = (_block[_position >> 3] >> (_position & 7)) & 1;
                value |= bit << i;
                _position++;
            }
            return value;
        }
    }

    static int Expand(int value, int bits) => bits >= 8 ? value : (value << (8 - bits)) | (value >> (2 * bits - 8));

    static int Interpolate(int e0, int e1, int weight) => ((64 - weight) * e0 + weight * e1 + 32) >> 6;

    /// <summary>Decodes one 16-byte block into 16 RGBA texels, row major (x fastest), 64 bytes.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        if (block.Length < BlockBytes) throw new ArgumentException("BC7 block is 16 bytes", nameof(block));
        if (rgba.Length < 64) throw new ArgumentException("16 RGBA texels are 64 bytes", nameof(rgba));

        var reader = new BitReader(block);
        int mode = 0;
        while (mode < 8 && reader.Read(1) == 0) mode++;
        if (mode == 8)
        {
            // Reserved: the specification says transparent black.
            rgba.Slice(0, 64).Clear();
            return;
        }

        int subsets = Modes[mode, 0], partitionBits = Modes[mode, 1], rotationBits = Modes[mode, 2];
        int indexSelectionBits = Modes[mode, 3], colourBits = Modes[mode, 4], alphaBits = Modes[mode, 5];
        int endpointPBits = Modes[mode, 6], sharedPBits = Modes[mode, 7];
        int indexBits = Modes[mode, 8], indexBits2 = Modes[mode, 9];

        int partition = partitionBits > 0 ? reader.Read(partitionBits) : 0;
        int rotation = rotationBits > 0 ? reader.Read(rotationBits) : 0;
        int indexSelection = indexSelectionBits > 0 ? reader.Read(indexSelectionBits) : 0;

        int endpointCount = subsets * 2;
        Span<int> endpoints = stackalloc int[6 * 4]; // [endpoint * 4 + channel]
        for (int channel = 0; channel < 3; channel++)
            for (int e = 0; e < endpointCount; e++)
                endpoints[e * 4 + channel] = reader.Read(colourBits);
        if (alphaBits > 0)
            for (int e = 0; e < endpointCount; e++)
                endpoints[e * 4 + 3] = reader.Read(alphaBits);

        // P-bits extend every channel of an endpoint (or of both endpoints of
        // a subset) by one low bit.
        if (endpointPBits > 0)
        {
            for (int e = 0; e < endpointCount; e++)
            {
                int p = reader.Read(1);
                for (int channel = 0; channel < 4; channel++)
                    endpoints[e * 4 + channel] = (endpoints[e * 4 + channel] << 1) | p;
            }
        }
        else if (sharedPBits > 0)
        {
            for (int s = 0; s < subsets; s++)
            {
                int p = reader.Read(1);
                for (int e = s * 2; e < s * 2 + 2; e++)
                    for (int channel = 0; channel < 4; channel++)
                        endpoints[e * 4 + channel] = (endpoints[e * 4 + channel] << 1) | p;
            }
        }
        int extra = endpointPBits + sharedPBits;
        int colourPrecision = colourBits + extra, alphaPrecision = alphaBits + extra;
        for (int e = 0; e < endpointCount; e++)
        {
            for (int channel = 0; channel < 3; channel++)
                endpoints[e * 4 + channel] = Expand(endpoints[e * 4 + channel], colourPrecision);
            endpoints[e * 4 + 3] = alphaBits > 0 ? Expand(endpoints[e * 4 + 3], alphaPrecision) : 255;
        }

        // Indices, primary then secondary; the anchor pixel of each subset
        // has one bit fewer.
        Span<int> primary = stackalloc int[16];
        Span<int> secondary = stackalloc int[16];
        for (int p = 0; p < 16; p++)
        {
            int subset = Subset(subsets, partition, p);
            int bits = indexBits - (IsAnchor(subsets, partition, subset, p) ? 1 : 0);
            primary[p] = reader.Read(bits);
        }
        if (indexBits2 > 0)
        {
            for (int p = 0; p < 16; p++)
            {
                int bits = indexBits2 - (p == 0 ? 1 : 0);
                secondary[p] = reader.Read(bits);
            }
        }

        for (int p = 0; p < 16; p++)
        {
            int subset = Subset(subsets, partition, p);
            int e0 = subset * 2, e1 = subset * 2 + 1;
            int colourIndex = primary[p], alphaIndex = primary[p];
            int colourWidth = indexBits, alphaWidth = indexBits;
            if (indexBits2 > 0)
            {
                // Mode 4 lets the wider index set serve colour instead of alpha.
                if (indexSelection == 0) { alphaIndex = secondary[p]; alphaWidth = indexBits2; }
                else { colourIndex = secondary[p]; colourWidth = indexBits2; }
            }
            int cw = Weight(colourWidth, colourIndex), aw = Weight(alphaWidth, alphaIndex);
            int r = Interpolate(endpoints[e0 * 4], endpoints[e1 * 4], cw);
            int g = Interpolate(endpoints[e0 * 4 + 1], endpoints[e1 * 4 + 1], cw);
            int b = Interpolate(endpoints[e0 * 4 + 2], endpoints[e1 * 4 + 2], cw);
            int a = alphaBits > 0 ? Interpolate(endpoints[e0 * 4 + 3], endpoints[e1 * 4 + 3], aw) : 255;
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }
            rgba[p * 4] = (byte)r;
            rgba[p * 4 + 1] = (byte)g;
            rgba[p * 4 + 2] = (byte)b;
            rgba[p * 4 + 3] = (byte)a;
        }
    }

    static int Weight(int width, int index) => width switch
    {
        2 => Weights2[index],
        3 => Weights3[index],
        _ => Weights4[index],
    };

    static int Subset(int subsets, int partition, int pixel) => subsets switch
    {
        1 => 0,
        2 => Partitions2[partition, pixel],
        _ => Partitions3[partition, pixel],
    };

    static bool IsAnchor(int subsets, int partition, int subset, int pixel)
    {
        if (subset == 0) return pixel == 0;
        if (subsets == 2) return pixel == AnchorSecondOfTwo[partition];
        return subset == 1 ? pixel == AnchorSecondOfThree[partition] : pixel == AnchorThirdOfThree[partition];
    }

    /// <summary>Decodes one mip level to RGBA32, width*height*4 bytes; partial edge blocks are clipped.</summary>
    public static byte[] DecodeLevel(ReadOnlySpan<byte> data, int width, int height)
    {
        int blocksWide = (width + 3) / 4, blocksHigh = (height + 3) / 4;
        long expected = (long)blocksWide * blocksHigh * BlockBytes;
        if (data.Length != expected)
            throw new InvalidDataException($"BC7 level {width}x{height} needs {expected} bytes, payload has {data.Length}");
        var rgba = new byte[width * height * 4];
        Span<byte> texels = stackalloc byte[64];
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                DecodeBlock(data.Slice((by * blocksWide + bx) * BlockBytes, BlockBytes), texels);
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
