// TextureFormats — Unity's TextureFormat ids and the arithmetic of their
// payloads, kept free of AssetsTools so it can be tested without a Unity file.
//
// Why this exists: the Linux depot is a desktop build and ships desktop
// block-compressed textures (DXT1/DXT5, and possibly BC7). No Android GPU
// this port runs on samples those formats -- neither Mali nor Adreno reports
// them -- so the player decompresses each one to RGBA32 on the CPU at load
// time and uploads that. A 4096x4096 DXT5 atlas is 16 MiB on disk and 64 MiB
// on the GPU. On a 3 GB handheld that expansion is the difference between the
// language-select screen and Android's low-memory killer.
//
// Everything here is a *capacity* figure: what a texture costs if it is
// loaded. Nothing here knows which textures a scene actually loads.
//
// The ETC2 targets are chosen for one property that makes a later conversion
// safe: block-for-block, they are the same size as the DXT formats they
// replace. DXT1 and ETC2 RGB are both 8 bytes per 4x4 block; DXT5 and ETC2
// RGBA8 are both 16. A payload swap therefore leaves every offset, stream
// size and m_CompleteImageSize untouched, which is what lets the swap be
// verified rather than trusted.

namespace BundleSurgery;

internal enum TextureFamily
{
    Dxt1,
    Dxt5,
    Dxt1Crunched,
    Dxt5Crunched,
    Bc4,
    Bc5,
    Bc6h,
    Bc7,
    Etc,
    Etc2,
    Astc,
    Pvrtc,
    Uncompressed,
    Unknown,
}

/// <summary>
/// One Unity texture format. <see cref="BlockWidth"/> and <see cref="BlockHeight"/>
/// are 1 for uncompressed formats, in which case <see cref="BlockBytes"/> is
/// bytes per pixel. For crunched formats the block figures describe the
/// format the crunch decoder produces, not the variable-size payload on disk.
/// </summary>
internal sealed record TextureFormatInfo(
    int Id,
    string Name,
    TextureFamily Family,
    int BlockWidth,
    int BlockHeight,
    int BlockBytes,
    bool HasAlpha)
{
    public bool IsKnown => Family != TextureFamily.Unknown;
    public bool IsBlockCompressed => IsKnown && BlockWidth > 1;
    public bool IsCrunched => Family is TextureFamily.Dxt1Crunched or TextureFamily.Dxt5Crunched;

    /// <summary>
    /// Formats that no Android GPU samples natively, which the player
    /// decompresses to RGBA32 (or a float format for BC6H) at load time.
    /// </summary>
    public bool IsDesktopOnly => Family is TextureFamily.Dxt1 or TextureFamily.Dxt5
        or TextureFamily.Dxt1Crunched or TextureFamily.Dxt5Crunched
        or TextureFamily.Bc4 or TextureFamily.Bc5 or TextureFamily.Bc6h or TextureFamily.Bc7;

    /// <summary>The on-disk payload size is fixed by the dimensions.</summary>
    public bool HasPredictableSize => IsKnown && !IsCrunched;
}

internal static class TextureFormats
{
    // UnityEngine.TextureFormat, as of Unity 6000.0. Ids are stable across
    // versions; new ones are only ever appended.
    public const int Alpha8 = 1;
    public const int ARGB4444 = 2;
    public const int RGB24 = 3;
    public const int RGBA32 = 4;
    public const int ARGB32 = 5;
    public const int RGB565 = 7;
    public const int R16 = 9;
    public const int DXT1 = 10;
    public const int DXT5 = 12;
    public const int RGBA4444 = 13;
    public const int BGRA32 = 14;
    public const int RHalf = 15;
    public const int RGHalf = 16;
    public const int RGBAHalf = 17;
    public const int RFloat = 18;
    public const int RGFloat = 19;
    public const int RGBAFloat = 20;
    public const int YUY2 = 21;
    public const int RGB9e5Float = 22;
    public const int BC6H = 24;
    public const int BC7 = 25;
    public const int BC4 = 26;
    public const int BC5 = 27;
    public const int DXT1Crunched = 28;
    public const int DXT5Crunched = 29;
    public const int PVRTC_RGB2 = 30;
    public const int PVRTC_RGBA2 = 31;
    public const int PVRTC_RGB4 = 32;
    public const int PVRTC_RGBA4 = 33;
    public const int ETC_RGB4 = 34;
    public const int EAC_R = 41;
    public const int EAC_R_SIGNED = 42;
    public const int EAC_RG = 43;
    public const int EAC_RG_SIGNED = 44;
    public const int ETC2_RGB = 45;
    public const int ETC2_RGBA1 = 46;
    public const int ETC2_RGBA8 = 47;
    public const int ASTC_4x4 = 48;
    public const int ASTC_5x5 = 49;
    public const int ASTC_6x6 = 50;
    public const int ASTC_8x8 = 51;
    public const int ASTC_10x10 = 52;
    public const int ASTC_12x12 = 53;
    // 54-59 were ASTC_RGBA_4x4..12x12 before Unity 2019 folded the RGB and
    // RGBA names together; a file written by an older Unity can still carry them.
    public const int ASTC_RGBA_4x4 = 54;
    public const int ASTC_RGBA_12x12 = 59;
    public const int RG16 = 62;
    public const int R8 = 63;
    public const int ETC_RGB4Crunched = 64;
    public const int ETC2_RGBA8Crunched = 65;
    public const int ASTC_HDR_4x4 = 66;
    public const int ASTC_HDR_5x5 = 67;
    public const int ASTC_HDR_6x6 = 68;
    public const int ASTC_HDR_8x8 = 69;
    public const int ASTC_HDR_10x10 = 70;
    public const int ASTC_HDR_12x12 = 71;
    public const int RG32 = 72;
    public const int RGB48 = 73;
    public const int RGBA64 = 74;
    public const int R8_SIGNED = 75;
    public const int RG16_SIGNED = 76;
    public const int RGB24_SIGNED = 77;
    public const int RGBA32_SIGNED = 78;
    public const int R16_SIGNED = 79;
    public const int RG32_SIGNED = 80;
    public const int RGB48_SIGNED = 81;
    public const int RGBA64_SIGNED = 82;

    static TextureFormatInfo Pixels(int id, string name, int bytesPerPixel, bool alpha) =>
        new(id, name, TextureFamily.Uncompressed, 1, 1, bytesPerPixel, alpha);

    static TextureFormatInfo Blocks(int id, string name, TextureFamily family, int block, int bytes, bool alpha) =>
        new(id, name, family, block, block, bytes, alpha);

    static readonly Dictionary<int, TextureFormatInfo> Table = new TextureFormatInfo[]
    {
        Pixels(Alpha8, "Alpha8", 1, true),
        Pixels(ARGB4444, "ARGB4444", 2, true),
        Pixels(RGB24, "RGB24", 3, false),
        Pixels(RGBA32, "RGBA32", 4, true),
        Pixels(ARGB32, "ARGB32", 4, true),
        Pixels(RGB565, "RGB565", 2, false),
        Pixels(R16, "R16", 2, false),
        Blocks(DXT1, "DXT1 (BC1)", TextureFamily.Dxt1, 4, 8, false),
        Blocks(DXT5, "DXT5 (BC3)", TextureFamily.Dxt5, 4, 16, true),
        Pixels(RGBA4444, "RGBA4444", 2, true),
        Pixels(BGRA32, "BGRA32", 4, true),
        Pixels(RHalf, "RHalf", 2, false),
        Pixels(RGHalf, "RGHalf", 4, false),
        Pixels(RGBAHalf, "RGBAHalf", 8, true),
        Pixels(RFloat, "RFloat", 4, false),
        Pixels(RGFloat, "RGFloat", 8, false),
        Pixels(RGBAFloat, "RGBAFloat", 16, true),
        Pixels(YUY2, "YUY2", 2, false),
        Pixels(RGB9e5Float, "RGB9e5Float", 4, false),
        Blocks(BC6H, "BC6H", TextureFamily.Bc6h, 4, 16, false),
        Blocks(BC7, "BC7", TextureFamily.Bc7, 4, 16, true),
        Blocks(BC4, "BC4", TextureFamily.Bc4, 4, 8, false),
        Blocks(BC5, "BC5", TextureFamily.Bc5, 4, 16, false),
        Blocks(DXT1Crunched, "DXT1Crunched", TextureFamily.Dxt1Crunched, 4, 8, false),
        Blocks(DXT5Crunched, "DXT5Crunched", TextureFamily.Dxt5Crunched, 4, 16, true),
        // PVRTC 2 bpp blocks are 8x4 texels; 4 bpp blocks are 4x4. Both 8 bytes.
        new(PVRTC_RGB2, "PVRTC_RGB2", TextureFamily.Pvrtc, 8, 4, 8, false),
        new(PVRTC_RGBA2, "PVRTC_RGBA2", TextureFamily.Pvrtc, 8, 4, 8, true),
        Blocks(PVRTC_RGB4, "PVRTC_RGB4", TextureFamily.Pvrtc, 4, 8, false),
        Blocks(PVRTC_RGBA4, "PVRTC_RGBA4", TextureFamily.Pvrtc, 4, 8, true),
        Blocks(ETC_RGB4, "ETC_RGB4", TextureFamily.Etc, 4, 8, false),
        Blocks(EAC_R, "EAC_R", TextureFamily.Etc2, 4, 8, false),
        Blocks(EAC_R_SIGNED, "EAC_R_SIGNED", TextureFamily.Etc2, 4, 8, false),
        Blocks(EAC_RG, "EAC_RG", TextureFamily.Etc2, 4, 16, false),
        Blocks(EAC_RG_SIGNED, "EAC_RG_SIGNED", TextureFamily.Etc2, 4, 16, false),
        Blocks(ETC2_RGB, "ETC2_RGB", TextureFamily.Etc2, 4, 8, false),
        Blocks(ETC2_RGBA1, "ETC2_RGBA1", TextureFamily.Etc2, 4, 8, true),
        Blocks(ETC2_RGBA8, "ETC2_RGBA8", TextureFamily.Etc2, 4, 16, true),
        Blocks(ASTC_4x4, "ASTC_4x4", TextureFamily.Astc, 4, 16, true),
        Blocks(ASTC_5x5, "ASTC_5x5", TextureFamily.Astc, 5, 16, true),
        Blocks(ASTC_6x6, "ASTC_6x6", TextureFamily.Astc, 6, 16, true),
        Blocks(ASTC_8x8, "ASTC_8x8", TextureFamily.Astc, 8, 16, true),
        Blocks(ASTC_10x10, "ASTC_10x10", TextureFamily.Astc, 10, 16, true),
        Blocks(ASTC_12x12, "ASTC_12x12", TextureFamily.Astc, 12, 16, true),
        Blocks(ASTC_RGBA_4x4, "ASTC_RGBA_4x4", TextureFamily.Astc, 4, 16, true),
        Blocks(55, "ASTC_RGBA_5x5", TextureFamily.Astc, 5, 16, true),
        Blocks(56, "ASTC_RGBA_6x6", TextureFamily.Astc, 6, 16, true),
        Blocks(57, "ASTC_RGBA_8x8", TextureFamily.Astc, 8, 16, true),
        Blocks(58, "ASTC_RGBA_10x10", TextureFamily.Astc, 10, 16, true),
        Blocks(ASTC_RGBA_12x12, "ASTC_RGBA_12x12", TextureFamily.Astc, 12, 16, true),
        Pixels(RG16, "RG16", 2, false),
        Pixels(R8, "R8", 1, false),
        Blocks(ETC_RGB4Crunched, "ETC_RGB4Crunched", TextureFamily.Etc, 4, 8, false),
        Blocks(ETC2_RGBA8Crunched, "ETC2_RGBA8Crunched", TextureFamily.Etc2, 4, 16, true),
        Blocks(ASTC_HDR_4x4, "ASTC_HDR_4x4", TextureFamily.Astc, 4, 16, true),
        Blocks(ASTC_HDR_5x5, "ASTC_HDR_5x5", TextureFamily.Astc, 5, 16, true),
        Blocks(ASTC_HDR_6x6, "ASTC_HDR_6x6", TextureFamily.Astc, 6, 16, true),
        Blocks(ASTC_HDR_8x8, "ASTC_HDR_8x8", TextureFamily.Astc, 8, 16, true),
        Blocks(ASTC_HDR_10x10, "ASTC_HDR_10x10", TextureFamily.Astc, 10, 16, true),
        Blocks(ASTC_HDR_12x12, "ASTC_HDR_12x12", TextureFamily.Astc, 12, 16, true),
        Pixels(RG32, "RG32", 4, false),
        Pixels(RGB48, "RGB48", 6, false),
        Pixels(RGBA64, "RGBA64", 8, true),
        Pixels(R8_SIGNED, "R8_SIGNED", 1, false),
        Pixels(RG16_SIGNED, "RG16_SIGNED", 2, false),
        Pixels(RGB24_SIGNED, "RGB24_SIGNED", 3, false),
        Pixels(RGBA32_SIGNED, "RGBA32_SIGNED", 4, true),
        Pixels(R16_SIGNED, "R16_SIGNED", 2, false),
        Pixels(RG32_SIGNED, "RG32_SIGNED", 4, false),
        Pixels(RGB48_SIGNED, "RGB48_SIGNED", 6, false),
        Pixels(RGBA64_SIGNED, "RGBA64_SIGNED", 8, true),
    }.ToDictionary(f => f.Id);

    public static TextureFormatInfo Describe(int id) =>
        Table.TryGetValue(id, out var info)
            ? info
            : new TextureFormatInfo(id, $"Unknown({id})", TextureFamily.Unknown, 0, 0, 0, false);

    /// <summary>
    /// Every crunched format decodes to a fixed-size sibling. The crunch
    /// payload itself is variable, so its size is not predictable, but what
    /// the GPU (or the RGBA32 fallback) ends up holding is.
    /// </summary>
    public static TextureFormatInfo Decoded(TextureFormatInfo f) => f.Id switch
    {
        DXT1Crunched => Describe(DXT1),
        DXT5Crunched => Describe(DXT5),
        ETC_RGB4Crunched => Describe(ETC_RGB4),
        ETC2_RGBA8Crunched => Describe(ETC2_RGBA8),
        _ => f,
    };

    /// <summary>Mip level dimension: halves each level, never below one.</summary>
    public static int MipDimension(int size, int level) => Math.Max(1, size >> level);

    /// <summary>
    /// Bytes one mip level of the given dimensions occupies in this format.
    /// Compressed formats round each dimension up to whole blocks -- a 2x2
    /// mip of a DXT texture is one full 8-byte block -- which is exactly what
    /// Unity stores and what makes sub-block levels work at all.
    /// </summary>
    public static long LevelSize(TextureFormatInfo f, int width, int height)
    {
        if (!f.IsKnown) return -1;
        if (width <= 0 || height <= 0) return 0;
        long bw = (width + f.BlockWidth - 1) / f.BlockWidth;
        long bh = (height + f.BlockHeight - 1) / f.BlockHeight;
        return bw * bh * f.BlockBytes;
    }

    /// <summary>The whole mip chain of one image, or -1 when the format's size is not predictable.</summary>
    public static long ChainSize(TextureFormatInfo f, int width, int height, int mipCount)
    {
        if (!f.HasPredictableSize) return -1;
        // A 0x0 texture (a dynamic font atlas before its first glyph) holds
        // nothing, not one texel.
        if (width <= 0 || height <= 0) return 0;
        if (mipCount < 1) mipCount = 1;
        long total = 0;
        for (int level = 0; level < mipCount; level++)
            total += LevelSize(f, MipDimension(width, level), MipDimension(height, level));
        return total;
    }

    /// <summary>What the same chain costs once the player has expanded it to RGBA32.</summary>
    public static long Rgba32ChainSize(int width, int height, int mipCount) =>
        ChainSize(Describe(RGBA32), width, height, mipCount);

    /// <summary>
    /// What a desktop-only texture costs on an Android GPU: the player keeps
    /// the RGBA32 expansion, and for BC6H a half-float one. Other formats
    /// cost what they cost.
    /// </summary>
    public static long AndroidResidentSize(TextureFormatInfo f, int width, int height, int mipCount)
    {
        if (!f.IsKnown) return -1;
        if (!f.IsDesktopOnly) return ChainSize(Decoded(f), width, height, mipCount);
        return f.Family == TextureFamily.Bc6h
            ? ChainSize(Describe(RGBAHalf), width, height, mipCount)
            : Rgba32ChainSize(width, height, mipCount);
    }

    /// <summary>
    /// The ETC2 format a same-size swap would produce, or null when no
    /// same-size mapping exists. DXT1 payloads that use the punch-through
    /// mode become ETC2_RGBA1 so that the transparent texels stay
    /// transparent; the rest become plain ETC2_RGB. Both are 8 bytes a block.
    /// </summary>
    public static TextureFormatInfo? SameSizeEtc2Target(TextureFormatInfo source, bool dxt1UsesPunchThrough) =>
        source.Family switch
        {
            TextureFamily.Dxt1 => Describe(dxt1UsesPunchThrough ? ETC2_RGBA1 : ETC2_RGB),
            TextureFamily.Dxt5 => Describe(ETC2_RGBA8),
            _ => null,
        };

    /// <summary>
    /// Why a texture is not a candidate for the same-size swap, or null when
    /// it is. Reported rather than acted on: nothing here converts anything.
    /// </summary>
    public static string? ConversionBlocker(TextureFormatInfo f, int dimension, int imageCount)
    {
        if (!f.IsKnown) return "unknown-format";
        if (dimension != TextureDimension2D) return $"dimension-{dimension}";
        if (imageCount != 1) return "image-count";
        return f.Family switch
        {
            TextureFamily.Dxt1 or TextureFamily.Dxt5 => null,
            TextureFamily.Dxt1Crunched or TextureFamily.Dxt5Crunched => "crunched",
            TextureFamily.Bc4 or TextureFamily.Bc5 => "bc4-bc5-channel-packed",
            TextureFamily.Bc6h or TextureFamily.Bc7 => "bc6h-bc7",
            _ => "not-desktop-only",
        };
    }

    // UnityEngine.Rendering.TextureDimension: 2 is Tex2D.
    public const int TextureDimension2D = 2;
}
