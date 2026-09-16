// Etc2 — an ETC2 encoder and decoder, written from the Khronos specification.
//
// Why write one: every Android GPU samples ETC2, and ETC2's three formats are
// exactly the size of the desktop formats the depot ships -- ETC2_RGB and
// ETC2_RGBA1 are 8 bytes a 4x4 block like DXT1, ETC2_RGBA8 is 16 like DXT5.
// Re-encoding a DXT payload into ETC2 therefore changes not one offset, size
// or length anywhere in the serialized file, which is what makes the swap
// verifiable rather than trusted. See TextureFormats.cs for the memory case.
//
// Why managed code rather than a native encoder: the conversion runs on the
// PC only, so speed matters less than two other things. It has to be
// deterministic on every machine so a patch manifest hash means the same
// thing everywhere -- integer arithmetic throughout, except one plane fit
// that .NET evaluates in IEEE double on SSE2 wherever this runs -- and it has
// to need nothing added to the Docker image. Nothing here is copied from
// another encoder; the block layouts and modifier tables are the
// specification's, and the technique of choosing the planar mode's spare bits
// so that only the blue channel overflows is the one every encoder uses.
//
// What it encodes. Each 4x4 block gets the better of two candidates:
//
//   ETC1 differential or individual mode, both flips, all eight modifier
//   tables, every texel taking its best of four modifiers. This is the mode
//   that carries sprite art: two sub-blocks, each a base colour and an
//   intensity ramp.
//
//   Planar mode, a least-squares plane through the block, for gradients,
//   where a two-colour ramp bands visibly.
//
// T and H modes are not emitted. They help blocks made of two unrelated
// colours; the differential mode's two sub-blocks cover most of that, and
// leaving them out keeps the encoder small enough to read in one sitting.
//
// The decoder reads everything the encoder writes and refuses T and H, which
// the encoder never produces. It exists so a conversion can be checked
// against the image it came from, and so the tests pin the bit layouts.
//
// Block words are 64-bit big-endian in memory: byte 0 holds bits 63..56.
// Texels are numbered as the specification numbers them, column-major:
// texel p is at x = p / 4, y = p % 4.

namespace BundleSurgery;

internal static class Etc2
{
    /// <summary>Part of every patch manifest; changing the encoder changes this.</summary>
    public const string EncoderVersion = "silksong-etc2-1";

    // Table C.9: intensity modifiers, {-b, -a, a, b} per codeword. A pixel
    // index of 0 selects +a, 1 selects +b, 2 selects -a, 3 selects -b.
    static readonly int[,] Modifiers =
    {
        { -8, -2, 2, 8 }, { -17, -5, 5, 17 }, { -29, -9, 9, 29 }, { -42, -13, 13, 42 },
        { -60, -18, 18, 60 }, { -80, -24, 24, 80 }, { -106, -33, 33, 106 }, { -183, -47, 47, 183 },
    };
    static readonly int[] IndexToSlot = { 2, 3, 1, 0 };

    // Table C.16: EAC alpha modifiers.
    static readonly int[,] AlphaModifiers =
    {
        { -3, -6, -9, -15, 2, 5, 8, 14 }, { -3, -7, -10, -13, 2, 6, 9, 12 },
        { -2, -5, -8, -13, 1, 4, 7, 12 }, { -2, -4, -6, -13, 1, 3, 5, 12 },
        { -3, -6, -8, -12, 2, 5, 7, 11 }, { -3, -7, -9, -11, 2, 6, 8, 10 },
        { -4, -7, -8, -11, 3, 6, 7, 10 }, { -3, -5, -8, -11, 2, 4, 7, 10 },
        { -2, -6, -8, -10, 1, 5, 7, 9 }, { -2, -5, -8, -10, 1, 4, 7, 9 },
        { -2, -4, -8, -10, 1, 3, 7, 9 }, { -2, -5, -7, -10, 1, 4, 6, 9 },
        { -3, -4, -7, -10, 2, 3, 6, 9 }, { -1, -2, -3, -10, 0, 1, 2, 9 },
        { -4, -6, -8, -9, 3, 5, 7, 8 }, { -3, -5, -7, -9, 2, 4, 6, 8 },
    };

    static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    static int Expand4(int v) => (v << 4) | v;
    static int Expand5(int v) => (v << 3) | (v >> 2);
    static int Expand6(int v) => (v << 2) | (v >> 4);
    static int Expand7(int v) => (v << 1) | (v >> 6);
    static int Quantize(int v, int max) => (v * max + 127) / 255;

    // ── level-level API ──────────────────────────────────────────────────────

    public static bool IsSupportedTarget(int format) =>
        format is TextureFormats.ETC2_RGB or TextureFormats.ETC2_RGBA1 or TextureFormats.ETC2_RGBA8;

    /// <summary>
    /// Encodes one mip level of RGBA32 texels (width*height*4 bytes, row
    /// major) into the given ETC2 format. Partial edge blocks are padded by
    /// repeating the edge texels, which the GPU never samples.
    /// </summary>
    public static byte[] EncodeLevel(int format, ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (!IsSupportedTarget(format)) throw new NotSupportedException($"format {format} is not an ETC2 target");
        if (width <= 0 || height <= 0) throw new ArgumentException("empty level");
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"{width}x{height} RGBA32 is {width * height * 4} bytes, got {rgba.Length}");

        int blocksWide = (width + 3) / 4, blocksHigh = (height + 3) / 4;
        int blockBytes = format == TextureFormats.ETC2_RGBA8 ? 16 : 8;
        var output = new byte[blocksWide * blocksHigh * blockBytes];
        Span<byte> texels = stackalloc byte[64];
        Span<byte> block = stackalloc byte[16];
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                GatherBlock(rgba, width, height, bx, by, texels);
                int at = (by * blocksWide + bx) * blockBytes;
                switch (format)
                {
                    case TextureFormats.ETC2_RGB:
                        EncodeRgbBlock(texels, block, punchThrough: false);
                        block.Slice(0, 8).CopyTo(output.AsSpan(at, 8));
                        break;
                    case TextureFormats.ETC2_RGBA1:
                        EncodeRgbBlock(texels, block, punchThrough: true);
                        block.Slice(0, 8).CopyTo(output.AsSpan(at, 8));
                        break;
                    default:
                        EncodeAlphaBlock(texels, block);
                        EncodeRgbBlock(texels, block.Slice(8), punchThrough: false, alphaSeparate: true);
                        block.CopyTo(output.AsSpan(at, 16));
                        break;
                }
            }
        }
        return output;
    }

    /// <summary>Decodes one mip level back to RGBA32. Throws on T/H mode blocks, which this encoder never writes.</summary>
    public static byte[] DecodeLevel(int format, ReadOnlySpan<byte> data, int width, int height)
    {
        if (!IsSupportedTarget(format)) throw new NotSupportedException($"format {format} is not an ETC2 format this decoder reads");
        int blocksWide = (width + 3) / 4, blocksHigh = (height + 3) / 4;
        int blockBytes = format == TextureFormats.ETC2_RGBA8 ? 16 : 8;
        long expected = (long)blocksWide * blocksHigh * blockBytes;
        if (data.Length != expected)
            throw new InvalidDataException($"ETC2 level {width}x{height} needs {expected} bytes, payload has {data.Length}");

        var rgba = new byte[width * height * 4];
        Span<byte> texels = stackalloc byte[64];
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                var block = data.Slice((by * blocksWide + bx) * blockBytes, blockBytes);
                if (format == TextureFormats.ETC2_RGBA8)
                {
                    DecodeRgbBlock(block.Slice(8), texels, punchThrough: false);
                    DecodeAlphaBlock(block, texels);
                }
                else
                {
                    DecodeRgbBlock(block, texels, punchThrough: format == TextureFormats.ETC2_RGBA1);
                }
                for (int p = 0; p < 16; p++)
                {
                    int x = bx * 4 + p / 4, y = by * 4 + p % 4;
                    if (x >= width || y >= height) continue;
                    int dst = (y * width + x) * 4;
                    rgba[dst] = texels[p * 4];
                    rgba[dst + 1] = texels[p * 4 + 1];
                    rgba[dst + 2] = texels[p * 4 + 2];
                    rgba[dst + 3] = texels[p * 4 + 3];
                }
            }
        }
        return rgba;
    }

    /// <summary>The block's 16 texels in specification order (column-major), edge-clamped.</summary>
    static void GatherBlock(ReadOnlySpan<byte> rgba, int width, int height, int bx, int by, Span<byte> texels)
    {
        for (int p = 0; p < 16; p++)
        {
            int x = Math.Min(bx * 4 + p / 4, width - 1), y = Math.Min(by * 4 + p % 4, height - 1);
            int src = (y * width + x) * 4;
            texels[p * 4] = rgba[src];
            texels[p * 4 + 1] = rgba[src + 1];
            texels[p * 4 + 2] = rgba[src + 2];
            texels[p * 4 + 3] = rgba[src + 3];
        }
    }

    // ── RGB blocks ───────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes 16 RGBA texels into an 8-byte ETC2 RGB block. With
    /// <paramref name="punchThrough"/> the block is an ETC2_RGBA1 block: texels
    /// with alpha below 128 become transparent, individual mode is unavailable
    /// (its bit is the opaque flag), and a block holding any transparent texel
    /// uses the opaque=0 modifier set.
    /// </summary>
    public static void EncodeRgbBlock(ReadOnlySpan<byte> texels, Span<byte> block, bool punchThrough, bool alphaSeparate = false)
    {
        // Texels that do not count: in punch-through mode those that will be
        // holes; with a separate alpha block (ETC2_RGBA8) those whose alpha
        // is zero, since nothing will ever see their colour. Both are left
        // out of the fit so a sprite's edge is not blended with its
        // invisible background.
        bool anyTransparent = false;
        Span<bool> transparent = stackalloc bool[16];
        for (int p = 0; p < 16; p++)
        {
            transparent[p] = punchThrough ? texels[p * 4 + 3] < 128 : alphaSeparate && texels[p * 4 + 3] == 0;
            anyTransparent |= transparent[p];
        }

        ulong best = 0;
        long bestError = long.MaxValue;

        // Planar first: it is one candidate. A punch-through block with
        // holes cannot use it (planar has no alpha); an RGBA8 block with
        // invisible texels can, fitted through the visible ones.
        if (!(punchThrough && anyTransparent))
        {
            ulong planar = EncodePlanar(texels, transparent, out long planarError);
            best = planar;
            bestError = planarError;
        }

        for (int flip = 0; flip < 2; flip++)
        {
            // Differential mode, and individual mode where it exists.
            for (int mode = 0; mode < 2; mode++)
            {
                bool differential = mode == 0;
                if (!differential && punchThrough) continue;
                ulong word = EncodeEtc1(texels, transparent, punchThrough && anyTransparent, punchThrough, flip, differential, out long error);
                if (error < bestError)
                {
                    bestError = error;
                    best = word;
                }
            }
        }
        WriteWord(block, best);
    }

    static void SubblockAverage(ReadOnlySpan<byte> texels, ReadOnlySpan<bool> transparent, int flip, int sub, out int r, out int g, out int b)
    {
        long sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int p = 0; p < 16; p++)
        {
            if (Subblock(p, flip) != sub || transparent[p]) continue;
            sr += texels[p * 4];
            sg += texels[p * 4 + 1];
            sb += texels[p * 4 + 2];
            n++;
        }
        if (n == 0) { r = g = b = 0; return; }
        r = (int)((sr + n / 2) / n);
        g = (int)((sg + n / 2) / n);
        b = (int)((sb + n / 2) / n);
    }

    // flip 0: sub-blocks are columns 0-1 and 2-3; flip 1: rows 0-1 and 2-3.
    static int Subblock(int p, int flip) => flip == 0 ? (p / 4) / 2 : (p % 4) / 2;

    static ulong EncodeEtc1(
        ReadOnlySpan<byte> texels, ReadOnlySpan<bool> transparent, bool opaqueBitZero, bool punchThrough,
        int flip, bool differential, out long totalError)
    {
        int levels = differential ? 31 : 15;
        Span<int> q = stackalloc int[6];       // the quantised base colour of each sub-block
        Span<int> tables = stackalloc int[2];
        Span<int> avg = stackalloc int[3];
        Span<int> candidate = stackalloc int[3];
        Span<int> expanded = stackalloc int[3];
        Span<int> residual = stackalloc int[3];
        ulong indexBits = 0;
        totalError = 0;

        for (int sub = 0; sub < 2; sub++)
        {
            SubblockAverage(texels, transparent, flip, sub, out avg[0], out avg[1], out avg[2]);
            for (int c = 0; c < 3; c++) candidate[c] = Quantize(avg[c], levels);
            ConstrainDelta(q, candidate, sub, differential);

            long bestErr = long.MaxValue;
            int bestTable = 0;
            ulong bestIdx = 0;
            Span<int> bestBase = stackalloc int[3];
            for (int t = 0; t < 8; t++)
            {
                // The base is the sub-block's mean, then once refined: the
                // mean of what is left after each texel takes its modifier.
                // One k-means step, which is where most of ETC1's quality is,
                // at twice the cost of none.
                Span<int> baseQ = stackalloc int[3];
                candidate.CopyTo(baseQ);
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int c = 0; c < 3; c++) expanded[c] = differential ? Expand5(baseQ[c]) : Expand4(baseQ[c]);
                    long err = Assign(texels, transparent, punchThrough, flip, sub, expanded, t, opaqueBitZero, out ulong idx, residual);
                    if (err < bestErr)
                    {
                        bestErr = err;
                        bestTable = t;
                        bestIdx = idx;
                        baseQ.CopyTo(bestBase);
                    }
                    if (pass == 1) break;
                    Span<int> refined = stackalloc int[3];
                    for (int c = 0; c < 3; c++) refined[c] = Quantize(Clamp255(residual[c]), levels);
                    ConstrainDelta(q, refined, sub, differential);
                    if (refined[0] == baseQ[0] && refined[1] == baseQ[1] && refined[2] == baseQ[2]) break;
                    refined.CopyTo(baseQ);
                }
            }
            q[sub * 3] = bestBase[0]; q[sub * 3 + 1] = bestBase[1]; q[sub * 3 + 2] = bestBase[2];
            tables[sub] = bestTable;
            indexBits |= bestIdx;
            totalError += bestErr;
        }

        ulong word;
        if (differential)
        {
            word = ((ulong)q[0] << 59) | ((ulong)((q[3] - q[0]) & 7) << 56)
                 | ((ulong)q[1] << 51) | ((ulong)((q[4] - q[1]) & 7) << 48)
                 | ((ulong)q[2] << 43) | ((ulong)((q[5] - q[2]) & 7) << 40);
            // Bit 33: the differential flag, or in punch-through textures the
            // opaque flag, which is 0 only for a block with transparent texels.
            if (!opaqueBitZero) word |= 1UL << 33;
        }
        else
        {
            word = ((ulong)q[0] << 60) | ((ulong)q[3] << 56)
                 | ((ulong)q[1] << 52) | ((ulong)q[4] << 48)
                 | ((ulong)q[2] << 44) | ((ulong)q[5] << 40);
        }
        if (flip == 1) word |= 1UL << 32;
        word |= (ulong)tables[0] << 37;
        word |= (ulong)tables[1] << 34;
        return word | indexBits;
    }

    /// <summary>
    /// In differential mode the second sub-block's base is a 3-bit delta from
    /// the first's. An out-of-range delta is clamped: the colour moves rather
    /// than the mode being abandoned, because in punch-through textures there
    /// is no individual mode to fall back to.
    /// </summary>
    static void ConstrainDelta(ReadOnlySpan<int> q, Span<int> candidate, int sub, bool differential)
    {
        if (!differential || sub == 0) return;
        for (int c = 0; c < 3; c++)
        {
            int d = candidate[c] - q[c];
            if (d < -4) d = -4;
            if (d > 3) d = 3;
            candidate[c] = q[c] + d;
        }
    }

    /// <summary>
    /// Gives every texel of the sub-block its best modifier from one table.
    /// Returns the squared error; <paramref name="residual"/> receives the
    /// mean of texel minus modifier per channel, the next base to try.
    /// </summary>
    static long Assign(
        ReadOnlySpan<byte> texels, ReadOnlySpan<bool> transparent, bool punchThrough, int flip, int sub,
        ReadOnlySpan<int> baseColour, int table, bool opaqueBitZero, out ulong indexBits, Span<int> residual)
    {
        long err = 0;
        ulong idx = 0;
        long rr = 0, rg = 0, rb = 0;
        int n = 0;
        for (int p = 0; p < 16; p++)
        {
            if (Subblock(p, flip) != sub) continue;
            int choice;
            if (transparent[p])
            {
                // A hole takes index 2 (transparent when the opaque bit is
                // 0); an invisible RGBA8 texel takes any index at no cost.
                choice = punchThrough ? 2 : 0;
            }
            else
            {
                choice = BestModifier(texels, p, baseColour[0], baseColour[1], baseColour[2], table, opaqueBitZero, out long e);
                err += e;
                int m = Modifier(table, choice, opaqueBitZero);
                rr += texels[p * 4] - m;
                rg += texels[p * 4 + 1] - m;
                rb += texels[p * 4 + 2] - m;
                n++;
            }
            idx |= (ulong)(choice & 1) << p;          // lsb plane, bits 15..0
            idx |= (ulong)(choice >> 1) << (16 + p);  // msb plane, bits 31..16
        }
        indexBits = idx;
        if (n > 0)
        {
            residual[0] = (int)((rr + n / 2) / n);
            residual[1] = (int)((rg + n / 2) / n);
            residual[2] = (int)((rb + n / 2) / n);
        }
        else
        {
            residual[0] = baseColour[0]; residual[1] = baseColour[1]; residual[2] = baseColour[2];
        }
        return err;
    }

    static int Modifier(int table, int index, bool opaqueBitZero)
    {
        if (!opaqueBitZero) return Modifiers[table, IndexToSlot[index]];
        // Opaque bit 0: the small modifiers become 0, index 2 is transparent.
        return index switch { 0 => 0, 1 => Modifiers[table, 3], 3 => Modifiers[table, 0], _ => 0 };
    }

    static int BestModifier(ReadOnlySpan<byte> texels, int p, int br, int bg, int bb, int table, bool opaqueBitZero, out long error)
    {
        int r = texels[p * 4], g = texels[p * 4 + 1], b = texels[p * 4 + 2];
        int best = 0;
        error = long.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            if (opaqueBitZero && i == 2) continue;
            int m = Modifier(table, i, opaqueBitZero);
            long dr = Clamp255(br + m) - r, dg = Clamp255(bg + m) - g, db = Clamp255(bb + m) - b;
            long e = dr * dr + dg * dg + db * db;
            if (e < error)
            {
                error = e;
                best = i;
            }
        }
        return best;
    }

    // ── planar ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Least-squares plane per channel, quantised to the mode's 6/7/6-bit
    /// origin, horizontal and vertical samples; the spare bits are then set so
    /// that decoding sees red and green in range and blue overflowing, which
    /// is how the mode is signalled.
    /// </summary>
    static ulong EncodePlanar(ReadOnlySpan<byte> texels, ReadOnlySpan<bool> ignored, out long error)
    {
        // Ignored texels are replaced by the mean of the rest, which keeps
        // the closed-form fit over the full grid valid and pulls the plane
        // towards nothing.
        Span<int> fill = stackalloc int[3];
        int visible = 0;
        for (int p = 0; p < 16; p++)
        {
            if (ignored[p]) continue;
            visible++;
            for (int c = 0; c < 3; c++) fill[c] += texels[p * 4 + c];
        }
        if (visible > 0) for (int c = 0; c < 3; c++) fill[c] = (fill[c] + visible / 2) / visible;

        Span<int> o = stackalloc int[3], h = stackalloc int[3], v = stackalloc int[3];
        for (int c = 0; c < 3; c++)
        {
            double sum = 0, sx = 0, sy = 0;
            for (int p = 0; p < 16; p++)
            {
                int x = p / 4, y = p % 4;
                double value = ignored[p] ? fill[c] : texels[p * 4 + c];
                sum += value;
                sx += (x - 1.5) * value;
                sy += (y - 1.5) * value;
            }
            // Σ(x-1.5)² over the 4x4 grid is 20.
            double slopeX = sx / 20.0, slopeY = sy / 20.0;
            double origin = sum / 16.0 - 1.5 * slopeX - 1.5 * slopeY;
            int max = c == 1 ? 127 : 63;
            o[c] = QuantizePlanar(origin, max);
            h[c] = QuantizePlanar(origin + 4 * slopeX, max);
            v[c] = QuantizePlanar(origin + 4 * slopeY, max);
        }

        ulong word = PackPlanar(o[0], o[1], o[2], h[0], h[1], h[2], v[0], v[1], v[2]);
        Span<byte> decoded = stackalloc byte[64];
        DecodePlanar(word, decoded);
        error = 0;
        for (int p = 0; p < 16; p++)
        {
            if (ignored[p]) continue;
            for (int c = 0; c < 3; c++)
            {
                long d = decoded[p * 4 + c] - texels[p * 4 + c];
                error += d * d;
            }
        }
        return word;
    }

    static int QuantizePlanar(double value, int max)
    {
        int q = (int)Math.Round(value * max / 255.0, MidpointRounding.AwayFromZero);
        return q < 0 ? 0 : q > max ? max : q;
    }

    /// <summary>Planar bit layout: RO 62..57, GO1 56, GO2 54..49, BO1 48, BO2 44..43, BO3 41..39, RH1 38..34, diff 33, RH2 32, GH 31..25, BH 24..19, RV 18..13, GV 12..6, BV 5..0.</summary>
    internal static ulong PackPlanar(int ro, int go, int bo, int rh, int gh, int bh, int rv, int gv, int bv)
    {
        ulong word = ((ulong)(uint)(ro & 0x3F) << 57)
            | ((ulong)(uint)((go >> 6) & 1) << 56) | ((ulong)(uint)(go & 0x3F) << 49)
            | ((ulong)(uint)((bo >> 5) & 1) << 48) | ((ulong)(uint)((bo >> 3) & 3) << 43) | ((ulong)(uint)(bo & 7) << 39)
            | ((ulong)(uint)((rh >> 1) & 0x1F) << 34) | (1UL << 33) | ((ulong)(uint)(rh & 1) << 32)
            | ((ulong)(uint)(gh & 0x7F) << 25) | ((ulong)(uint)(bh & 0x3F) << 19)
            | ((ulong)(uint)(rv & 0x3F) << 13) | ((ulong)(uint)(gv & 0x7F) << 6) | (ulong)(uint)(bv & 0x3F);

        // Red: R1 is bits 63..59 = {bit63, RO[5..2]}, dR is bits 58..56 =
        // {RO[1], RO[0], GO1}. Copying dR's sign into bit 63 keeps the sum in
        // range whatever the data bits are. Green likewise through bit 55.
        if ((ro & 2) != 0) word |= 1UL << 63;
        if ((go & 2) != 0) word |= 1UL << 55;
        // Blue must overflow. B1 is bits 47..43 = {47, 46, 45, BO2}, dB is
        // bits 42..40 = {42, BO3[2], BO3[1]}. With x = BO2 and y = BO3 >> 1:
        // either 000/negative puts the sum at x + y - 4, below zero when
        // x + y < 4, or 111/positive puts it at 28 + x + y, past 31 otherwise.
        int x = (bo >> 3) & 3, y = (bo & 7) >> 1;
        if (x + y < 4) word |= 1UL << 42;
        else word |= 7UL << 45;
        return word;
    }

    static void DecodePlanar(ulong w, Span<byte> texels)
    {
        int ro = (int)((w >> 57) & 0x3F);
        int go = (int)(((w >> 56) & 1) << 6 | ((w >> 49) & 0x3F));
        int bo = (int)(((w >> 48) & 1) << 5 | ((w >> 43) & 3) << 3 | ((w >> 39) & 7));
        int rh = (int)(((w >> 34) & 0x1F) << 1 | ((w >> 32) & 1));
        int gh = (int)((w >> 25) & 0x7F);
        int bh = (int)((w >> 19) & 0x3F);
        int rv = (int)((w >> 13) & 0x3F);
        int gv = (int)((w >> 6) & 0x7F);
        int bv = (int)(w & 0x3F);
        ro = Expand6(ro); rh = Expand6(rh); rv = Expand6(rv);
        go = Expand7(go); gh = Expand7(gh); gv = Expand7(gv);
        bo = Expand6(bo); bh = Expand6(bh); bv = Expand6(bv);
        for (int p = 0; p < 16; p++)
        {
            int x = p / 4, y = p % 4;
            texels[p * 4] = (byte)Clamp255((x * (rh - ro) + y * (rv - ro) + 4 * ro + 2) >> 2);
            texels[p * 4 + 1] = (byte)Clamp255((x * (gh - go) + y * (gv - go) + 4 * go + 2) >> 2);
            texels[p * 4 + 2] = (byte)Clamp255((x * (bh - bo) + y * (bv - bo) + 4 * bo + 2) >> 2);
            texels[p * 4 + 3] = 255;
        }
    }

    // ── RGB decode ───────────────────────────────────────────────────────────

    public static void DecodeRgbBlock(ReadOnlySpan<byte> block, Span<byte> texels, bool punchThrough)
    {
        ulong w = ReadWord(block);
        bool diffBit = ((w >> 33) & 1) != 0;
        bool differential = diffBit || punchThrough;
        bool opaqueBitZero = punchThrough && !diffBit;

        Span<int> baseColour = stackalloc int[6];
        if (differential)
        {
            int r1 = (int)((w >> 59) & 0x1F), g1 = (int)((w >> 51) & 0x1F), b1 = (int)((w >> 43) & 0x1F);
            int dr = Signed3((int)((w >> 56) & 7)), dg = Signed3((int)((w >> 48) & 7)), db = Signed3((int)((w >> 40) & 7));
            int r2 = r1 + dr, g2 = g1 + dg, b2 = b1 + db;
            if (r2 < 0 || r2 > 31) throw new NotSupportedException("ETC2 T mode is not produced by this encoder");
            if (g2 < 0 || g2 > 31) throw new NotSupportedException("ETC2 H mode is not produced by this encoder");
            if (b2 < 0 || b2 > 31)
            {
                DecodePlanar(w, texels);
                return;
            }
            baseColour[0] = Expand5(r1); baseColour[1] = Expand5(g1); baseColour[2] = Expand5(b1);
            baseColour[3] = Expand5(r2); baseColour[4] = Expand5(g2); baseColour[5] = Expand5(b2);
        }
        else
        {
            baseColour[0] = Expand4((int)((w >> 60) & 15)); baseColour[3] = Expand4((int)((w >> 56) & 15));
            baseColour[1] = Expand4((int)((w >> 52) & 15)); baseColour[4] = Expand4((int)((w >> 48) & 15));
            baseColour[2] = Expand4((int)((w >> 44) & 15)); baseColour[5] = Expand4((int)((w >> 40) & 15));
        }
        int table0 = (int)((w >> 37) & 7), table1 = (int)((w >> 34) & 7);
        int flip = (int)((w >> 32) & 1);
        for (int p = 0; p < 16; p++)
        {
            int sub = Subblock(p, flip);
            int index = (int)(((w >> (16 + p)) & 1) << 1 | ((w >> p) & 1));
            if (opaqueBitZero && index == 2)
            {
                texels[p * 4] = texels[p * 4 + 1] = texels[p * 4 + 2] = texels[p * 4 + 3] = 0;
                continue;
            }
            int m = Modifier(sub == 0 ? table0 : table1, index, opaqueBitZero);
            texels[p * 4] = (byte)Clamp255(baseColour[sub * 3] + m);
            texels[p * 4 + 1] = (byte)Clamp255(baseColour[sub * 3 + 1] + m);
            texels[p * 4 + 2] = (byte)Clamp255(baseColour[sub * 3 + 2] + m);
            texels[p * 4 + 3] = 255;
        }
    }

    static int Signed3(int v) => v >= 4 ? v - 8 : v;

    // ── EAC alpha ────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes the alpha channel of 16 texels into an 8-byte EAC block: base
    /// value, multiplier, table, and 3-bit indices. Each table is tried at the
    /// two multipliers nearest the block's range and three bases around its
    /// midpoint; the least-error combination wins.
    /// </summary>
    public static void EncodeAlphaBlock(ReadOnlySpan<byte> texels, Span<byte> block)
    {
        int min = 255, max = 0;
        for (int p = 0; p < 16; p++)
        {
            int a = texels[p * 4 + 3];
            if (a < min) min = a;
            if (a > max) max = a;
        }
        if (min == max)
        {
            block[0] = (byte)min;
            for (int i = 1; i < 8; i++) block[i] = 0;
            return;
        }

        long bestError = long.MaxValue;
        int bestBase = 0, bestMult = 1, bestTable = 0;
        ulong bestIdx = 0;
        Span<int> sorted = stackalloc int[8];
        for (int t = 0; t < 16; t++)
        {
            int lo = AlphaModifiers[t, 3], hi = AlphaModifiers[t, 7];
            double span = hi - lo;
            double ideal = (max - min) / span;
            int m0 = (int)Math.Floor(ideal), m1 = (int)Math.Ceiling(ideal);
            for (int mult = Math.Max(1, m0); mult <= Math.Min(15, Math.Max(m1, 1)); mult++)
            {
                int centre = (int)Math.Round((min + max) / 2.0 - mult * (hi + lo) / 2.0, MidpointRounding.AwayFromZero);
                for (int baseValue = centre - 1; baseValue <= centre + 1; baseValue++)
                {
                    if (baseValue < 0 || baseValue > 255) continue;
                    long err = 0;
                    ulong idx = 0;
                    for (int p = 0; p < 16 && err < bestError; p++)
                    {
                        int a = texels[p * 4 + 3];
                        int bi = 0;
                        long be = long.MaxValue;
                        for (int i = 0; i < 8; i++)
                        {
                            long d = Clamp255(baseValue + mult * AlphaModifiers[t, i]) - a;
                            long e = d * d;
                            if (e < be) { be = e; bi = i; }
                        }
                        err += be;
                        idx |= (ulong)(uint)bi << (45 - 3 * p);
                    }
                    if (err < bestError)
                    {
                        bestError = err;
                        bestBase = baseValue;
                        bestMult = mult;
                        bestTable = t;
                        bestIdx = idx;
                    }
                }
            }
        }
        block[0] = (byte)bestBase;
        block[1] = (byte)((bestMult << 4) | bestTable);
        for (int i = 0; i < 6; i++) block[2 + i] = (byte)(bestIdx >> (40 - 8 * i));
    }

    public static void DecodeAlphaBlock(ReadOnlySpan<byte> block, Span<byte> texels)
    {
        int baseValue = block[0];
        int mult = block[1] >> 4, table = block[1] & 15;
        ulong idx = 0;
        for (int i = 0; i < 6; i++) idx |= (ulong)block[2 + i] << (40 - 8 * i);
        for (int p = 0; p < 16; p++)
        {
            int i = (int)((idx >> (45 - 3 * p)) & 7);
            texels[p * 4 + 3] = (byte)Clamp255(baseValue + mult * AlphaModifiers[table, i]);
        }
    }

    // ── words ────────────────────────────────────────────────────────────────

    static ulong ReadWord(ReadOnlySpan<byte> block)
    {
        ulong w = 0;
        for (int i = 0; i < 8; i++) w = (w << 8) | block[i];
        return w;
    }

    static void WriteWord(Span<byte> block, ulong w)
    {
        for (int i = 0; i < 8; i++) block[i] = (byte)(w >> (56 - 8 * i));
    }
}
