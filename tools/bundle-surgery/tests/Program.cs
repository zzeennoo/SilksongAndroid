using System.Text.Json;
using BundleSurgery;

namespace BundleSurgery.Tests;

internal static class Program
{
    static int Main()
    {
        (string Name, Action Run)[] tests =
        {
            ("DXT1 and DXT5 block sizes", BlockSizes),
            ("odd and sub-4x4 dimensions round up to whole blocks", OddDimensions),
            ("complete mip chain sizes", MipChains),
            ("RGBA32 fallback cost includes the mip chain", Rgba32Cost),
            ("same-size ETC2 targets preserve payload sizes", SameSizeTargets),
            ("RGB versus alpha target selection", TargetSelection),
            ("every required format family is distinguished", Families),
            ("unknown format ids are reported, never guessed", UnknownFormat),
            ("crunched payload sizes are unpredictable but their cost is not", Crunched),
            ("conversion blockers name the reason", Blockers),
            ("DXT1 punch-through detection", PunchThrough),
            ("DXT1 payload must be whole blocks", TruncatedPayload),
            ("DXT1 colour decode", DecodeDxt1),
            ("DXT5 alpha decode", DecodeDxt5),
            ("level decode clips partial edge blocks", DecodeClips),
            ("level decode rejects a short payload", DecodeShort),
            ("serialized file sniff", Sniff),
            ("report entries survive a JSON round trip", JsonRoundTrip),
            ("report summary groups by format and totals savings", Summary),
            ("report summary output is deterministic", Deterministic),
            ("walker over a real fixture (optional)", RealFixture),
            ("ETC2 planar word pins the signalling bits", PlanarBits),
            ("ETC2 RGB round trip stays close to the source", Etc2RgbRoundTrip),
            ("ETC2 RGBA8 round trip keeps alpha", Etc2Rgba8RoundTrip),
            ("ETC2 RGBA1 keeps holes and opaque texels", Etc2Rgba1RoundTrip),
            ("ETC2 encodes sub-4x4 and odd levels at the right size", Etc2OddLevels),
            ("ETC2 output is deterministic", Etc2Deterministic),
            ("DXT to ETC2 chain keeps every level's size", DxtToEtc2Chain),
            ("ETC2 dump for an external decoder (optional)", Etc2Dump),
            ("transcode keeps a DXT chain's exact length", TranscodeChain),
            ("transcode picks RGBA1 for punch-through DXT1", TranscodeTarget),
            ("patch pack round trip, audit and tamper detection", PackRoundTrip),
            ("resource range patching leaves every other byte alone", RangePatch),
            ("apply and verify on a real serialized file (optional)", ApplySerialized),
            ("BC7 mode 6 solid block decodes to its endpoint", Bc7Solid),
            ("BC7 mode 5 rotation moves alpha into a colour channel", Bc7Rotation),
            ("BC7 reserved mode decodes to transparent black", Bc7Reserved),
            ("BC7 level decode clips and rejects short payloads", Bc7Level),
            ("BC7 to ETC2_RGBA8 keeps the chain length", Bc7Transcode),
            ("BC7 random-block dump for an external decoder (optional)", Bc7Dump),
        };
        int failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine("PASS " + name);
            }
            catch (SkipException e)
            {
                Console.WriteLine("SKIP " + name + ": " + e.Message);
            }
            catch (Exception e)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}\n{e}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} bundle-surgery tests passed");
        return failed == 0 ? 0 : 1;
    }

    sealed class SkipException : Exception { public SkipException(string m) : base(m) { } }

    static void Eq<T>(T expected, T actual, string what)
    {
        if (!Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static void True(bool value, string what)
    {
        if (!value) throw new Exception(what);
    }

    static readonly TextureFormatInfo Dxt1 = TextureFormats.Describe(TextureFormats.DXT1);
    static readonly TextureFormatInfo Dxt5 = TextureFormats.Describe(TextureFormats.DXT5);
    static readonly TextureFormatInfo Etc2Rgb = TextureFormats.Describe(TextureFormats.ETC2_RGB);
    static readonly TextureFormatInfo Etc2Rgba1 = TextureFormats.Describe(TextureFormats.ETC2_RGBA1);
    static readonly TextureFormatInfo Etc2Rgba8 = TextureFormats.Describe(TextureFormats.ETC2_RGBA8);

    static void BlockSizes()
    {
        Eq(8L, TextureFormats.LevelSize(Dxt1, 4, 4), "DXT1 one block");
        Eq(16L, TextureFormats.LevelSize(Dxt5, 4, 4), "DXT5 one block");
        Eq(32768L, TextureFormats.LevelSize(Dxt1, 256, 256), "DXT1 256^2");
        Eq(65536L, TextureFormats.LevelSize(Dxt5, 256, 256), "DXT5 256^2");
        Eq(8L * 1024 * 1024, TextureFormats.LevelSize(Dxt1, 4096, 4096), "DXT1 4096^2 is 8 MiB");
        Eq(16L * 1024 * 1024, TextureFormats.LevelSize(Dxt5, 4096, 4096), "DXT5 4096^2 is 16 MiB");
        Eq(64L * 1024 * 1024, TextureFormats.LevelSize(TextureFormats.Describe(TextureFormats.RGBA32), 4096, 4096), "RGBA32 4096^2 is 64 MiB");
        Eq(16L, TextureFormats.LevelSize(TextureFormats.Describe(TextureFormats.ASTC_6x6), 6, 6), "ASTC 6x6 one block");
        Eq(32L, TextureFormats.LevelSize(TextureFormats.Describe(TextureFormats.ASTC_6x6), 7, 6), "ASTC 6x6 two blocks wide");
    }

    static void OddDimensions()
    {
        Eq(8L, TextureFormats.LevelSize(Dxt1, 1, 1), "1x1 DXT1 is one block");
        Eq(8L, TextureFormats.LevelSize(Dxt1, 2, 2), "2x2 DXT1 is one block");
        Eq(16L, TextureFormats.LevelSize(Dxt5, 1, 1), "1x1 DXT5 is one block");
        Eq(32L, TextureFormats.LevelSize(Dxt5, 5, 3), "5x3 DXT5 is two blocks");
        Eq(24L, TextureFormats.LevelSize(Dxt1, 9, 4), "9x4 DXT1 is three blocks");
        Eq(0L, TextureFormats.LevelSize(Dxt1, 0, 4), "zero dimension is empty");
        Eq(0L, TextureFormats.ChainSize(TextureFormats.Describe(TextureFormats.RGBA32), 0, 0, 1), "0x0 chain is empty, not one texel");
        Eq(1, TextureFormats.MipDimension(1, 5), "mip dimension never below one");
        Eq(3, TextureFormats.MipDimension(7, 1), "mip dimension halves");
    }

    static void MipChains()
    {
        // 4096 -> 1 is 13 levels.
        long expected = 0;
        for (int level = 0; level < 13; level++)
        {
            int d = Math.Max(1, 4096 >> level);
            expected += (long)((d + 3) / 4) * ((d + 3) / 4) * 16;
        }
        Eq(expected, TextureFormats.ChainSize(Dxt5, 4096, 4096, 13), "DXT5 full chain");
        // Sub-block tail: 4,2,1 are one block each.
        Eq(8L * 3, TextureFormats.ChainSize(Dxt1, 4, 4, 3), "DXT1 4x4 with 3 mips");
        // A non-square chain.
        Eq(TextureFormats.LevelSize(Dxt1, 1024, 512) + TextureFormats.LevelSize(Dxt1, 512, 256),
            TextureFormats.ChainSize(Dxt1, 1024, 512, 2), "DXT1 1024x512 two levels");
        Eq(TextureFormats.LevelSize(Dxt1, 64, 64), TextureFormats.ChainSize(Dxt1, 64, 64, 0), "mip count 0 means one level");
    }

    static void Rgba32Cost()
    {
        Eq(64L * 1024 * 1024, TextureFormats.Rgba32ChainSize(4096, 4096, 1), "no mips");
        long chain = TextureFormats.Rgba32ChainSize(4096, 4096, 13);
        True(chain > 64L * 1024 * 1024 && chain < 64L * 1024 * 1024 * 4 / 3 + 64, "mip chain is about four thirds");
        Eq(chain, TextureFormats.AndroidResidentSize(Dxt5, 4096, 4096, 13), "DXT5 costs RGBA32 on Android");
        Eq(chain, TextureFormats.AndroidResidentSize(Dxt1, 4096, 4096, 13), "DXT1 costs RGBA32 on Android");
        Eq(TextureFormats.ChainSize(Etc2Rgba8, 4096, 4096, 13),
            TextureFormats.AndroidResidentSize(Etc2Rgba8, 4096, 4096, 13), "ETC2 costs itself");
        Eq(TextureFormats.ChainSize(TextureFormats.Describe(TextureFormats.RGBAHalf), 256, 256, 1),
            TextureFormats.AndroidResidentSize(TextureFormats.Describe(TextureFormats.BC6H), 256, 256, 1), "BC6H expands to half floats");
    }

    static void SameSizeTargets()
    {
        foreach (var (w, h, mips) in new[] { (4096, 4096, 13), (1024, 512, 1), (5, 3, 3), (1, 1, 1), (2048, 64, 12) })
        {
            Eq(TextureFormats.ChainSize(Dxt1, w, h, mips), TextureFormats.ChainSize(Etc2Rgb, w, h, mips), $"DXT1==ETC2_RGB {w}x{h}");
            Eq(TextureFormats.ChainSize(Dxt1, w, h, mips), TextureFormats.ChainSize(Etc2Rgba1, w, h, mips), $"DXT1==ETC2_RGBA1 {w}x{h}");
            Eq(TextureFormats.ChainSize(Dxt5, w, h, mips), TextureFormats.ChainSize(Etc2Rgba8, w, h, mips), $"DXT5==ETC2_RGBA8 {w}x{h}");
        }
    }

    static void TargetSelection()
    {
        Eq(TextureFormats.ETC2_RGB, TextureFormats.SameSizeEtc2Target(Dxt1, false)!.Id, "opaque DXT1");
        Eq(TextureFormats.ETC2_RGBA1, TextureFormats.SameSizeEtc2Target(Dxt1, true)!.Id, "punch-through DXT1");
        Eq(TextureFormats.ETC2_RGBA8, TextureFormats.SameSizeEtc2Target(Dxt5, false)!.Id, "DXT5");
        Eq(TextureFormats.ETC2_RGBA8, TextureFormats.SameSizeEtc2Target(Dxt5, true)!.Id, "DXT5 ignores the flag");
        Eq(TextureFormats.ETC2_RGBA8, TextureFormats.SameSizeEtc2Target(TextureFormats.Describe(TextureFormats.BC7), false)!.Id, "BC7 is 16 bytes a block, like ETC2_RGBA8");
        True(TextureFormats.SameSizeEtc2Target(TextureFormats.Describe(TextureFormats.BC6H), false) == null, "BC6H has no same-size target");
        True(TextureFormats.SameSizeEtc2Target(TextureFormats.Describe(TextureFormats.BC5), false) == null, "BC5 has no same-size target");
        True(TextureFormats.SameSizeEtc2Target(TextureFormats.Describe(TextureFormats.DXT5Crunched), false) == null, "crunched has no same-size target");
        True(TextureFormats.SameSizeEtc2Target(TextureFormats.Describe(TextureFormats.RGBA32), false) == null, "uncompressed has no target");
    }

    static void Families()
    {
        var expected = new (int, TextureFamily, string)[]
        {
            (TextureFormats.DXT1, TextureFamily.Dxt1, "DXT1 (BC1)"),
            (TextureFormats.DXT5, TextureFamily.Dxt5, "DXT5 (BC3)"),
            (TextureFormats.DXT1Crunched, TextureFamily.Dxt1Crunched, "DXT1Crunched"),
            (TextureFormats.DXT5Crunched, TextureFamily.Dxt5Crunched, "DXT5Crunched"),
            (TextureFormats.BC4, TextureFamily.Bc4, "BC4"),
            (TextureFormats.BC5, TextureFamily.Bc5, "BC5"),
            (TextureFormats.BC6H, TextureFamily.Bc6h, "BC6H"),
            (TextureFormats.BC7, TextureFamily.Bc7, "BC7"),
            (TextureFormats.ETC_RGB4, TextureFamily.Etc, "ETC_RGB4"),
            (TextureFormats.ETC2_RGBA8, TextureFamily.Etc2, "ETC2_RGBA8"),
            (TextureFormats.ASTC_6x6, TextureFamily.Astc, "ASTC_6x6"),
            (TextureFormats.RGBA32, TextureFamily.Uncompressed, "RGBA32"),
            (TextureFormats.RGB24, TextureFamily.Uncompressed, "RGB24"),
        };
        foreach (var (id, family, name) in expected)
        {
            var f = TextureFormats.Describe(id);
            Eq(family, f.Family, $"family of {id}");
            Eq(name, f.Name, $"name of {id}");
        }
        True(Dxt1.IsDesktopOnly && Dxt5.IsDesktopOnly && TextureFormats.Describe(TextureFormats.BC7).IsDesktopOnly, "desktop-only");
        True(!Etc2Rgb.IsDesktopOnly && !TextureFormats.Describe(TextureFormats.RGBA32).IsDesktopOnly, "android-native");
        True(!Dxt1.HasAlpha && Dxt5.HasAlpha && Etc2Rgba1.HasAlpha, "alpha flags");
    }

    static void UnknownFormat()
    {
        var f = TextureFormats.Describe(999);
        Eq(TextureFamily.Unknown, f.Family, "family");
        Eq("Unknown(999)", f.Name, "name");
        Eq(-1L, TextureFormats.ChainSize(f, 64, 64, 1), "size unknown");
        Eq(-1L, TextureFormats.AndroidResidentSize(f, 64, 64, 1), "cost unknown");
        Eq("unknown-format", TextureFormats.ConversionBlocker(f, 2, 1), "blocked");
    }

    static void Crunched()
    {
        var f = TextureFormats.Describe(TextureFormats.DXT5Crunched);
        Eq(-1L, TextureFormats.ChainSize(f, 512, 512, 1), "crunched size is not predictable");
        Eq(TextureFormats.Rgba32ChainSize(512, 512, 1), TextureFormats.AndroidResidentSize(f, 512, 512, 1), "but its Android cost is");
        Eq(TextureFormats.DXT5, TextureFormats.Decoded(f).Id, "decodes to DXT5");
        Eq("crunched", TextureFormats.ConversionBlocker(f, 2, 1), "blocked");
    }

    static void Blockers()
    {
        Eq(null, TextureFormats.ConversionBlocker(Dxt1, 2, 1), "DXT1 2D");
        Eq(null, TextureFormats.ConversionBlocker(Dxt5, 2, 1), "DXT5 2D");
        Eq("dimension-3", TextureFormats.ConversionBlocker(Dxt5, 3, 1), "3D");
        Eq("dimension-4", TextureFormats.ConversionBlocker(Dxt5, 4, 6), "cube");
        Eq("image-count", TextureFormats.ConversionBlocker(Dxt5, 2, 6), "six images");
        Eq(null, TextureFormats.ConversionBlocker(TextureFormats.Describe(TextureFormats.BC7), 2, 1), "BC7 2D");
        Eq("bc6h", TextureFormats.ConversionBlocker(TextureFormats.Describe(TextureFormats.BC6H), 2, 1), "BC6H");
        Eq("bc4-bc5-channel-packed", TextureFormats.ConversionBlocker(TextureFormats.Describe(TextureFormats.BC5), 2, 1), "BC5");
        Eq("not-desktop-only", TextureFormats.ConversionBlocker(Etc2Rgba8, 2, 1), "already ETC2");
    }

    static byte[] Dxt1Block(ushort c0, ushort c1, uint indices)
    {
        return new[]
        {
            (byte)c0, (byte)(c0 >> 8), (byte)c1, (byte)(c1 >> 8),
            (byte)indices, (byte)(indices >> 8), (byte)(indices >> 16), (byte)(indices >> 24),
        };
    }

    const ushort Red = 0xF800, Blue = 0x001F, Black = 0x0000;

    static void PunchThrough()
    {
        // Four-colour mode: index 3 is a colour, not a hole.
        True(!Dxt.Dxt1BlockUsesPunchThrough(Dxt1Block(Red, Blue, 0xFFFFFFFF)), "c0 > c1 never punches through");
        // Three-colour mode without index 3.
        True(!Dxt.Dxt1BlockUsesPunchThrough(Dxt1Block(Blue, Red, 0xAAAAAAAA)), "c0 <= c1 with indices 2 only");
        True(!Dxt.Dxt1BlockUsesPunchThrough(Dxt1Block(Red, Red, 0x00000000)), "equal colours, index 0");
        // Three-colour mode with one index 3.
        True(Dxt.Dxt1BlockUsesPunchThrough(Dxt1Block(Blue, Red, 0x00000003)), "first texel transparent");
        True(Dxt.Dxt1BlockUsesPunchThrough(Dxt1Block(Red, Red, 0xC0000000)), "last texel transparent, equal colours");

        var payload = Dxt1Block(Red, Blue, 0xFFFFFFFF).Concat(Dxt1Block(Blue, Red, 0x3)).Concat(Dxt1Block(Blue, Red, 0xC0000000)).ToArray();
        Eq(2, Dxt.CountDxt1PunchThroughBlocks(payload), "two of three blocks");
        Eq(0, Dxt.CountDxt1PunchThroughBlocks(Array.Empty<byte>()), "empty payload");
    }

    static void TruncatedPayload()
    {
        bool threw = false;
        try { Dxt.CountDxt1PunchThroughBlocks(new byte[12]); }
        catch (InvalidDataException) { threw = true; }
        True(threw, "12 bytes is not whole blocks");
    }

    static void DecodeDxt1()
    {
        var rgba = new byte[64];
        Dxt.DecodeColourBlock(Dxt1Block(Red, Blue, 0x00000000), rgba, fourColour: false);
        Eq((byte)255, rgba[0], "red r"); Eq((byte)0, rgba[1], "red g"); Eq((byte)0, rgba[2], "red b"); Eq((byte)255, rgba[3], "opaque");
        Dxt.DecodeColourBlock(Dxt1Block(Red, Blue, 0x55555555), rgba, fourColour: false);
        Eq((byte)0, rgba[0], "blue r"); Eq((byte)255, rgba[2], "blue b");
        // Index 2 in four-colour mode: two thirds c0.
        Dxt.DecodeColourBlock(Dxt1Block(Red, Blue, 0xAAAAAAAA), rgba, fourColour: false);
        Eq((byte)170, rgba[0], "2/3 red"); Eq((byte)85, rgba[2], "1/3 blue"); Eq((byte)255, rgba[3], "opaque");
        // Three-colour mode: index 2 is the midpoint, index 3 transparent black.
        Dxt.DecodeColourBlock(Dxt1Block(Blue, Red, 0xAAAAAAAA), rgba, fourColour: false);
        Eq((byte)127, rgba[0], "midpoint r"); Eq((byte)127, rgba[2], "midpoint b");
        Dxt.DecodeColourBlock(Dxt1Block(Blue, Red, 0xFFFFFFFF), rgba, fourColour: false);
        Eq((byte)0, rgba[0], "hole r"); Eq((byte)0, rgba[3], "hole alpha");
        // The same block read as a DXT5 colour half is always four-colour.
        Dxt.DecodeColourBlock(Dxt1Block(Blue, Red, 0xFFFFFFFF), rgba, fourColour: true);
        Eq((byte)255, rgba[3], "no hole in four-colour mode");
        Eq((byte)170, rgba[0], "1/3 blue + 2/3 red -> r");
    }

    static void DecodeDxt5()
    {
        var block = new byte[16];
        block[0] = 255; block[1] = 0;      // a0 > a1: eight-alpha mode
        // Alpha indices: texel 0 -> 0 (a0), texel 1 -> 1 (a1), texel 2 -> 2 ((6*255+0)/7)
        ulong idx = 0UL | (1UL << 3) | (2UL << 6);
        for (int i = 0; i < 6; i++) block[2 + i] = (byte)(idx >> (8 * i));
        Array.Copy(Dxt1Block(Red, Blue, 0x00000000), 0, block, 8, 8);
        var rgba = new byte[64];
        Dxt.DecodeDxt5Block(block, rgba);
        Eq((byte)255, rgba[3], "texel 0 alpha a0");
        Eq((byte)0, rgba[7], "texel 1 alpha a1");
        Eq((byte)219, rgba[11], "texel 2 alpha 6/7");
        Eq((byte)255, rgba[0], "colour still red");

        block[0] = 0; block[1] = 255;      // a0 <= a1: six-alpha mode with 0 and 255 sentinels
        idx = 6UL | (7UL << 3);
        for (int i = 0; i < 6; i++) block[2 + i] = (byte)(idx >> (8 * i));
        Dxt.DecodeDxt5Block(block, rgba);
        Eq((byte)0, rgba[3], "index 6 is transparent");
        Eq((byte)255, rgba[7], "index 7 is opaque");
    }

    static void DecodeClips()
    {
        // 5x3 DXT1: two blocks wide, one high; only 15 texels come out.
        var payload = Dxt1Block(Red, Blue, 0).Concat(Dxt1Block(Blue, Red, 0)).ToArray();
        var rgba = Dxt.DecodeLevel(Dxt1, payload, 5, 3);
        Eq(5 * 3 * 4, rgba.Length, "output size");
        Eq((byte)255, rgba[0], "(0,0) red");
        Eq((byte)0, rgba[(0 * 5 + 4) * 4], "(4,0) from second block: blue r");
        Eq((byte)255, rgba[(0 * 5 + 4) * 4 + 2], "(4,0) blue b");
        Eq((byte)255, rgba[(2 * 5 + 0) * 4], "(0,2) red");
    }

    static void DecodeShort()
    {
        bool threw = false;
        try { Dxt.DecodeLevel(Dxt5, new byte[16], 8, 4); }
        catch (InvalidDataException) { threw = true; }
        True(threw, "8x4 DXT5 needs 32 bytes");
    }

    static void Sniff()
    {
        string dir = Path.Combine(Path.GetTempPath(), "silksong-sniff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // metadata size, file size, version 22, data offset... only the version matters.
            var header = new byte[32];
            header[8] = 0; header[9] = 0; header[10] = 0; header[11] = 22;
            File.WriteAllBytes(Path.Combine(dir, "sharedassets0.assets"), header);
            var bundle = new byte[32];
            "UnityFS"u8.CopyTo(bundle);
            bundle[11] = 22;
            File.WriteAllBytes(Path.Combine(dir, "x.bundle"), bundle);
            File.WriteAllBytes(Path.Combine(dir, "sharedassets0.assets.resS"), header);
            File.WriteAllBytes(Path.Combine(dir, "random.bin"), header);
            var big = new byte[32]; big[8] = 0x7F; big[9] = 0xFF;
            File.WriteAllBytes(Path.Combine(dir, "level0"), big);
            var found = TextureReport.FindSerializedFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
            Eq("sharedassets0.assets", string.Join(",", found), "only the serialized file");
            True(!TextureReport.LooksLikeSerializedFile(Path.Combine(dir, "x.bundle")), "bundles are not serialized files");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static TextureEntry Entry(string name, int format, int w, int h, int mips, string? blocker = null, long payload = -1)
    {
        var f = TextureFormats.Describe(format);
        var e = new TextureEntry
        {
            File = "aa/" + name + ".bundle", Container = "bundle", AssetFile = "CAB-" + name, PathId = 1, Name = name,
            Width = w, Height = h, Dimension = 2, FormatId = format, Format = f.Name, Family = f.Family.ToString(),
            MipCount = mips, ImageCount = 1,
            PayloadBytes = payload >= 0 ? payload : Math.Max(0, TextureFormats.ChainSize(f, w, h, mips)),
        };
        e.DataLocation = e.PayloadBytes > 0 ? "stream" : "none";
        e.StreamPath = e.PayloadBytes > 0 ? "archive:/CAB/CAB.resS" : null;
        // Same derivation the walker does, minus the file access.
        TextureReport.ResolvePayload(e, null, scanPayloads: false, _ => new TextureReport.PayloadSource(long.MaxValue, (_, _) => Array.Empty<byte>()));
        if (blocker != null) e.ConversionBlocker = blocker;
        return e;
    }

    static void JsonRoundTrip()
    {
        var e = Entry("atlas", TextureFormats.DXT5, 4096, 4096, 1);
        e.Dxt1PunchThrough = null;
        string json = JsonSerializer.Serialize(new[] { e }, TextureReport.JsonOptions);
        True(json.Contains("\"androidResidentBytes\": 67108864"), "camelCase resident bytes: " + json);
        True(json.Contains("\"dxt1PunchThrough\": null"), "nullable kept");
        var back = JsonSerializer.Deserialize<TextureEntry[]>(json, TextureReport.JsonOptions)!;
        Eq(e.Name, back[0].Name, "name");
        Eq(e.AndroidResidentBytes, back[0].AndroidResidentBytes, "resident");
        Eq(e.Etc2Bytes, back[0].Etc2Bytes, "etc2");
        Eq(e.SuggestedTargetId, back[0].SuggestedTargetId, "target");
        Eq(e.EstimatedSavingBytes, back[0].EstimatedSavingBytes, "saving");
    }

    static void Summary()
    {
        var entries = new List<TextureEntry>
        {
            Entry("atlas-a", TextureFormats.DXT5, 4096, 4096, 1),
            Entry("atlas-b", TextureFormats.DXT1, 1024, 1024, 1),
            Entry("bc7", TextureFormats.BC7, 512, 512, 1),
            Entry("crunched", TextureFormats.DXT5Crunched, 256, 256, 1, payload: 1000),
            Entry("native", TextureFormats.ETC2_RGBA8, 256, 256, 1),
            Entry("short", TextureFormats.DXT5, 64, 64, 1, payload: 100),
            Entry("empty-font", TextureFormats.RGBA32, 0, 0, 1, payload: 0),
        };
        var d = TextureReport.Summarize(entries, "/root", payloadsScanned: false);
        Eq(7, d.Totals.Textures, "count");
        Eq(5, d.Totals.DesktopOnlyTextures, "desktop-only: two DXT5, DXT1, BC7, crunched");
        Eq(3, d.Totals.Convertible, "DXT5 + DXT1 + BC7");
        Eq(4, d.Totals.Blocked, "crunched, native, short, empty");
        Eq(0, d.Totals.PayloadUnreadable, "an empty texture is not an unlocated payload");
        True(!d.Blockers.ContainsKey("bc6h-bc7") && !d.Blockers.ContainsKey("bc6h"), "BC7 is not blocked");
        Eq(1, d.Blockers["crunched"], "crunched blocker");
        Eq(2, d.Blockers["not-desktop-only"], "native and empty blockers");
        Eq(1, d.Blockers["payload-size-mismatch"], "short blocker");
        Eq(1, d.Totals.PayloadMismatches, "mismatch counted");
        long atlasA = 64L * 1024 * 1024 - 16L * 1024 * 1024;
        long atlasB = 4L * 1024 * 1024 - 512L * 1024;
        long bc7 = 512L * 512 * 4 - 512L * 512;
        Eq(atlasA + atlasB + bc7, d.Totals.EstimatedSavingBytes, "saving over convertible only");
        Eq(16L * 1024 * 1024 + 512L * 1024 + 512L * 512, d.Totals.Etc2Bytes, "etc2 bytes over convertible only");
        True(d.Totals.BlockedSavingBytes >= 256L * 256 * 4 - 256L * 256, "crunched saving counted as blocked");
        var dxt5 = d.ByFormat.Single(s => s.FormatId == TextureFormats.DXT5);
        Eq(2, dxt5.Count, "two DXT5"); Eq(1, dxt5.Convertible, "one convertible DXT5"); Eq(1, dxt5.Blocked, "one blocked DXT5");
        Eq(TextureFormats.DXT5, d.ByFormat[0].FormatId, "largest format first");
        var sw = new StringWriter();
        TextureReport.PrintSummary(d, sw);
        True(sw.ToString().Contains("NOT the set of textures resident"), "scope label printed");
        True(sw.ToString().Contains("atlas-a"), "largest texture listed");
    }

    static void Deterministic()
    {
        var a = new List<TextureEntry> { Entry("b", TextureFormats.DXT1, 64, 64, 1), Entry("a", TextureFormats.DXT5, 64, 64, 1) };
        var b = new List<TextureEntry> { Entry("a", TextureFormats.DXT5, 64, 64, 1), Entry("b", TextureFormats.DXT1, 64, 64, 1) };
        var da = TextureReport.Summarize(a, "/root", false);
        var db = TextureReport.Summarize(b, "/root", false);
        da.GeneratedUtc = db.GeneratedUtc = "";
        Eq(JsonSerializer.Serialize(da, TextureReport.JsonOptions), JsonSerializer.Serialize(db, TextureReport.JsonOptions), "order-independent JSON");
    }

    static void RealFixture()
    {
        string? fixture = Environment.GetEnvironmentVariable("SILKSONG_TEXTURE_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture)) throw new SkipException("SILKSONG_TEXTURE_FIXTURE not set");
        if (!File.Exists(fixture) && !Directory.Exists(fixture)) throw new SkipException("fixture not present: " + fixture);
        string dir = Path.Combine(Path.GetTempPath(), "silksong-texture-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string root = Directory.Exists(fixture) ? fixture : Path.GetDirectoryName(Path.GetFullPath(fixture))!;
            string json = Path.Combine(dir, "report.json");
            string classData = Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
            int code = TextureReport.Run(root, json, scanPayloads: true, classData);
            Eq(0, code, "exit code");
            var doc = JsonSerializer.Deserialize<TextureReportDocument>(File.ReadAllText(json), TextureReport.JsonOptions)!;
            True(doc.Textures.Count > 0, "found textures");
            True(doc.Textures.All(t => t.ExpectedBytes < 0 || t.PayloadMatchesExpected || t.PayloadIssue != null || t.ConversionBlocker != null),
                "every mismatch is explained");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── ETC2 ────────────────────────────────────────────────────────────────

    static byte[] TestImage(int width, int height, int seed, bool alpha, bool holes)
    {
        // A gradient with a hard-edged shape and some noise: gradients want
        // planar mode, the shape wants sub-blocks, the noise stops either
        // from being trivially exact.
        var rgba = new byte[width * height * 4];
        var rng = new Random(seed);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            bool inShape = (x - width / 2) * (x - width / 2) + (y - height / 2) * (y - height / 2) < (width * height) / 6;
            rgba[i] = (byte)Math.Clamp(x * 255 / Math.Max(1, width - 1) + rng.Next(-6, 7), 0, 255);
            rgba[i + 1] = (byte)Math.Clamp(y * 255 / Math.Max(1, height - 1) + rng.Next(-6, 7), 0, 255);
            rgba[i + 2] = (byte)(inShape ? 200 : 40);
            rgba[i + 3] = (byte)(holes && ((x / 3 + y / 5) % 4 == 0) ? 0 : alpha ? Math.Clamp(128 + (x - y) * 4, 0, 255) : 255);
        }
        return rgba;
    }

    static double Psnr(byte[] a, byte[] b, int channels, int stride, bool skipTransparent)
    {
        double sum = 0; long n = 0;
        for (int i = 0; i < a.Length; i += stride)
        {
            if (skipTransparent && a[i + 3] < 128) continue;
            for (int c = 0; c < channels; c++) { double d = a[i + c] - b[i + c]; sum += d * d; n++; }
        }
        if (n == 0) return double.PositiveInfinity;
        double mse = sum / n;
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    static void PlanarBits()
    {
        // Every combination of the blue bits that steer the overflow, and a
        // few red/green values that steer the non-overflow.
        for (int bo = 0; bo < 64; bo++)
        for (int ro = 0; ro < 64; ro += 7)
        for (int go = 0; go < 128; go += 13)
        {
            ulong w = Etc2.PackPlanar(ro, go, bo, 63 - ro, 127 - go, 63 - bo, ro, go, bo);
            int r1 = (int)((w >> 59) & 0x1F), dr = (int)((w >> 56) & 7); if (dr >= 4) dr -= 8;
            int g1 = (int)((w >> 51) & 0x1F), dg = (int)((w >> 48) & 7); if (dg >= 4) dg -= 8;
            int b1 = (int)((w >> 43) & 0x1F), db = (int)((w >> 40) & 7); if (db >= 4) db -= 8;
            True(r1 + dr is >= 0 and <= 31, $"red must not overflow (ro={ro})");
            True(g1 + dg is >= 0 and <= 31, $"green must not overflow (go={go})");
            True(b1 + db is < 0 or > 31, $"blue must overflow (bo={bo})");
            True(((w >> 33) & 1) == 1, "diff bit set");
        }
        // A flat plane decodes back to itself.
        var texels = new byte[64];
        for (int p = 0; p < 16; p++) { texels[p * 4] = 0x84; texels[p * 4 + 1] = 0x42; texels[p * 4 + 2] = 0xC6; texels[p * 4 + 3] = 255; }
        var block = new byte[8];
        Etc2.EncodeRgbBlock(texels, block, punchThrough: false);
        var back = new byte[64];
        Etc2.DecodeRgbBlock(block, back, punchThrough: false);
        for (int p = 0; p < 16; p++)
        {
            True(Math.Abs(back[p * 4] - 0x84) <= 4 && Math.Abs(back[p * 4 + 1] - 0x42) <= 2 && Math.Abs(back[p * 4 + 2] - 0xC6) <= 4, "flat block within quantisation");
        }
    }

    static void Etc2RgbRoundTrip()
    {
        var src = TestImage(64, 48, 1, alpha: false, holes: false);
        var encoded = Etc2.EncodeLevel(TextureFormats.ETC2_RGB, src, 64, 48);
        Eq(16 * 12 * 8, encoded.Length, "size");
        var back = Etc2.DecodeLevel(TextureFormats.ETC2_RGB, encoded, 64, 48);
        double psnr = Psnr(src, back, 3, 4, false);
        // The test image has a hard blue edge inside sub-blocks, which ETC1's
        // ramps cannot follow; that is what T/H modes are for and they are
        // not emitted. 27 dB is what this encoder does on it.
        True(psnr > 27, $"RGB PSNR {psnr:0.0} dB on the hard-edged image");
        for (int i = 3; i < back.Length; i += 4) True(back[i] == 255, "opaque");
        // A smooth image is where planar and the ramps shine.
        var smooth = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        {
            int i = (y * 64 + x) * 4;
            smooth[i] = (byte)(x * 4); smooth[i + 1] = (byte)(y * 4); smooth[i + 2] = (byte)(128 + (x + y)); smooth[i + 3] = 255;
        }
        var smoothBack = Etc2.DecodeLevel(TextureFormats.ETC2_RGB, Etc2.EncodeLevel(TextureFormats.ETC2_RGB, smooth, 64, 64), 64, 64);
        double smoothPsnr = Psnr(smooth, smoothBack, 3, 4, false);
        True(smoothPsnr > 40, $"smooth PSNR {smoothPsnr:0.0} dB");
        Console.WriteLine($"      hard-edged {psnr:0.0} dB, smooth {smoothPsnr:0.0} dB");
    }

    static void Etc2Rgba8RoundTrip()
    {
        var src = TestImage(64, 64, 2, alpha: true, holes: false);
        var encoded = Etc2.EncodeLevel(TextureFormats.ETC2_RGBA8, src, 64, 64);
        Eq(16 * 16 * 16, encoded.Length, "size");
        var back = Etc2.DecodeLevel(TextureFormats.ETC2_RGBA8, encoded, 64, 64);
        // RGB is compared where it can be seen: under alpha 0 the encoder
        // spends nothing, by design.
        double rgb = Psnr(src, back, 3, 4, true);
        True(rgb > 27, $"visible RGB PSNR {rgb:0.0} dB");
        // A sprite: hard shape on a fully transparent background. The colour
        // under alpha 0 is not fitted, so the visible edge is not blended
        // with the background and stays sharp.
        var sprite = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            int i = (y * 32 + x) * 4;
            bool inside = (x - 16) * (x - 16) + (y - 16) * (y - 16) < 120;
            sprite[i] = (byte)(inside ? 220 : 0); sprite[i + 1] = (byte)(inside ? 40 + x * 3 : 0); sprite[i + 2] = (byte)(inside ? 60 : 0);
            sprite[i + 3] = (byte)(inside ? 255 : 0);
        }
        var spriteBack = Etc2.DecodeLevel(TextureFormats.ETC2_RGBA8, Etc2.EncodeLevel(TextureFormats.ETC2_RGBA8, sprite, 32, 32), 32, 32);
        double visible = Psnr(sprite, spriteBack, 3, 4, true);
        True(visible > 36, $"visible sprite PSNR {visible:0.0} dB");
        Console.WriteLine($"      hard-edged {rgb:0.0} dB, sprite (visible texels) {visible:0.0} dB");
        double alpha = 0; long n = 0;
        for (int i = 3; i < src.Length; i += 4) { double d = src[i] - back[i]; alpha += d * d; n++; }
        double alphaPsnr = 10 * Math.Log10(255.0 * 255.0 / Math.Max(1e-9, alpha / n));
        True(alphaPsnr > 36, $"alpha PSNR {alphaPsnr:0.0} dB");
        // A flat alpha block is exact.
        var flat = new byte[64]; for (int p = 0; p < 16; p++) flat[p * 4 + 3] = 77;
        var block = new byte[8]; Etc2.EncodeAlphaBlock(flat, block);
        var dec = new byte[64]; Etc2.DecodeAlphaBlock(block, dec);
        for (int p = 0; p < 16; p++) Eq((byte)77, dec[p * 4 + 3], "flat alpha exact");
    }

    static void Etc2Rgba1RoundTrip()
    {
        var src = TestImage(64, 64, 3, alpha: false, holes: true);
        var encoded = Etc2.EncodeLevel(TextureFormats.ETC2_RGBA1, src, 64, 64);
        Eq(16 * 16 * 8, encoded.Length, "size");
        var back = Etc2.DecodeLevel(TextureFormats.ETC2_RGBA1, encoded, 64, 64);
        for (int i = 3; i < src.Length; i += 4)
            Eq(src[i] < 128 ? (byte)0 : (byte)255, back[i], $"alpha at texel {i / 4} is one bit");
        double psnr = Psnr(src, back, 3, 4, true);
        True(psnr > 27, $"opaque RGB PSNR {psnr:0.0} dB");
        // A fully opaque image in RGBA1 must use opaque=1 blocks and decode opaque.
        var opaque = TestImage(16, 16, 4, alpha: false, holes: false);
        var back2 = Etc2.DecodeLevel(TextureFormats.ETC2_RGBA1, Etc2.EncodeLevel(TextureFormats.ETC2_RGBA1, opaque, 16, 16), 16, 16);
        for (int i = 3; i < back2.Length; i += 4) Eq((byte)255, back2[i], "opaque stays opaque");
    }

    static void Etc2OddLevels()
    {
        foreach (var (w, h) in new[] { (1, 1), (2, 2), (3, 5), (5, 3), (4, 1), (9, 4), (7, 7) })
        {
            var src = TestImage(w, h, w * 31 + h, alpha: true, holes: false);
            foreach (int f in new[] { TextureFormats.ETC2_RGB, TextureFormats.ETC2_RGBA1, TextureFormats.ETC2_RGBA8 })
            {
                var enc = Etc2.EncodeLevel(f, src, w, h);
                Eq(TextureFormats.LevelSize(TextureFormats.Describe(f), w, h), (long)enc.Length, $"{w}x{h} format {f}");
                var back = Etc2.DecodeLevel(f, enc, w, h);
                Eq(w * h * 4, back.Length, "decoded size");
            }
        }
    }

    static void Etc2Deterministic()
    {
        var src = TestImage(32, 32, 9, alpha: true, holes: true);
        foreach (int f in new[] { TextureFormats.ETC2_RGB, TextureFormats.ETC2_RGBA1, TextureFormats.ETC2_RGBA8 })
        {
            var a = Etc2.EncodeLevel(f, src, 32, 32);
            var b = Etc2.EncodeLevel(f, src, 32, 32);
            True(a.AsSpan().SequenceEqual(b), $"format {f} encodes identically twice");
        }
        // Pinned: a change in the encoder must show up here, and in EncoderVersion.
        var pinned = Etc2.EncodeLevel(TextureFormats.ETC2_RGB, TestImage(8, 8, 5, false, false), 8, 8);
        string hex = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pinned)).ToLowerInvariant();
        Console.WriteLine("      pinned ETC2_RGB 8x8 digest " + hex);
        Eq("silksong-etc2-1", Etc2.EncoderVersion, "encoder version");
    }

    static void DxtToEtc2Chain()
    {
        // Build a DXT5 mip chain by hand (any bytes are a valid DXT5 payload),
        // decode each level, encode to ETC2_RGBA8 and check the chain sizes match.
        int w = 20, h = 12, mips = 5;
        var rng = new Random(11);
        long total = 0;
        var chain = new List<byte[]>();
        for (int level = 0; level < mips; level++)
        {
            int lw = TextureFormats.MipDimension(w, level), lh = TextureFormats.MipDimension(h, level);
            var payload = new byte[TextureFormats.LevelSize(Dxt5, lw, lh)];
            rng.NextBytes(payload);
            var rgba = Dxt.DecodeLevel(Dxt5, payload, lw, lh);
            var etc = Etc2.EncodeLevel(TextureFormats.ETC2_RGBA8, rgba, lw, lh);
            Eq(payload.Length, etc.Length, $"level {level} {lw}x{lh}");
            chain.Add(etc);
            total += etc.Length;
        }
        Eq(TextureFormats.ChainSize(Dxt5, w, h, mips), total, "whole chain");
        Eq(TextureFormats.ChainSize(Etc2Rgba8, w, h, mips), total, "as ETC2");
    }

    static void Etc2Dump()
    {
        string? dir = Environment.GetEnvironmentVariable("SILKSONG_ETC2_DUMP");
        if (string.IsNullOrWhiteSpace(dir)) throw new SkipException("SILKSONG_ETC2_DUMP not set");
        Directory.CreateDirectory(dir);
        int w = 64, h = 48;
        var opaque = TestImage(w, h, 21, alpha: false, holes: false);
        var alpha = TestImage(w, h, 22, alpha: true, holes: false);
        var holes = TestImage(w, h, 23, alpha: false, holes: true);
        File.WriteAllBytes(Path.Combine(dir, "src-rgb.rgba"), opaque);
        File.WriteAllBytes(Path.Combine(dir, "src-rgba8.rgba"), alpha);
        File.WriteAllBytes(Path.Combine(dir, "src-rgba1.rgba"), holes);
        File.WriteAllBytes(Path.Combine(dir, "etc2-rgb.bin"), Etc2.EncodeLevel(TextureFormats.ETC2_RGB, opaque, w, h));
        File.WriteAllBytes(Path.Combine(dir, "etc2-rgba8.bin"), Etc2.EncodeLevel(TextureFormats.ETC2_RGBA8, alpha, w, h));
        File.WriteAllBytes(Path.Combine(dir, "etc2-rgba1.bin"), Etc2.EncodeLevel(TextureFormats.ETC2_RGBA1, holes, w, h));
        File.WriteAllText(Path.Combine(dir, "dims.txt"), $"{w} {h}\n");
    }

    // ── transcode and patch packs ───────────────────────────────────────────

    static TextureEntry DxtEntry(int format, int w, int h, int mips, byte[] payload, int? punchBlocks = null)
    {
        var e = Entry("tex", format, w, h, mips, payload: payload.Length);
        if (punchBlocks != null) { e.Dxt1PunchThrough = punchBlocks > 0; e.Dxt1PunchThroughBlocks = punchBlocks.Value; }
        var f = TextureFormats.Describe(format);
        e.SuggestedTargetId = TextureFormats.SameSizeEtc2Target(f, e.Dxt1PunchThrough == true)!.Id;
        return e;
    }

    static void TranscodeChain()
    {
        var rng = new Random(5);
        int w = 36, h = 20, mips = 6;
        var payload = new byte[TextureFormats.ChainSize(Dxt5, w, h, mips)];
        rng.NextBytes(payload);
        var entry = DxtEntry(TextureFormats.DXT5, w, h, mips, payload);
        var etc = TextureTranscode.Transcode(entry, payload);
        Eq(payload.Length, etc.Length, "same length");
        Eq(TextureFormats.ETC2_RGBA8, entry.SuggestedTargetId!.Value, "target");
        // Every level decodes at its own size.
        int at = 0;
        for (int level = 0; level < mips; level++)
        {
            int lw = TextureFormats.MipDimension(w, level), lh = TextureFormats.MipDimension(h, level);
            int size = (int)TextureFormats.LevelSize(Etc2Rgba8, lw, lh);
            var back = Etc2.DecodeLevel(TextureFormats.ETC2_RGBA8, etc.AsSpan(at, size), lw, lh);
            Eq(lw * lh * 4, back.Length, $"level {level}");
            at += size;
        }
        // A short payload is refused, never padded.
        bool threw = false;
        try { TextureTranscode.Transcode(entry, payload.AsSpan(0, payload.Length - 8).ToArray()); }
        catch (InvalidDataException) { threw = true; }
        True(threw, "short payload rejected");
    }

    static void TranscodeTarget()
    {
        // 8x8 DXT1: four blocks, one of them with a transparent texel.
        var payload = Dxt1Block(Red, Blue, 0).Concat(Dxt1Block(Blue, Red, 0x3)).Concat(Dxt1Block(Red, Blue, 0)).Concat(Dxt1Block(Red, Blue, 0)).ToArray();
        var entry = DxtEntry(TextureFormats.DXT1, 8, 8, 1, payload, punchBlocks: 1);
        Eq(TextureFormats.ETC2_RGBA1, entry.SuggestedTargetId!.Value, "punch-through target");
        var etc = TextureTranscode.Transcode(entry, payload);
        var back = Etc2.DecodeLevel(TextureFormats.ETC2_RGBA1, etc, 8, 8);
        var src = Dxt.DecodeLevel(Dxt1, payload, 8, 8);
        for (int i = 3; i < src.Length; i += 4) Eq(src[i] == 0 ? (byte)0 : (byte)255, back[i], $"alpha kept at {i / 4}");
        var opaque = DxtEntry(TextureFormats.DXT1, 8, 8, 1, payload, punchBlocks: 0);
        Eq(TextureFormats.ETC2_RGB, opaque.SuggestedTargetId!.Value, "opaque target");
    }

    static string MakePack(string dir, TexturePatchManifest manifest, Dictionary<string, byte[]> blobs)
    {
        string blobRoot = Path.Combine(dir, "blobs");
        Directory.CreateDirectory(blobRoot);
        foreach (var (digest, bytes) in blobs) File.WriteAllBytes(Path.Combine(blobRoot, digest + ".bin"), bytes);
        string pack = Path.Combine(dir, "textures.zip");
        TextureTranscode.WritePack(pack, manifest, blobRoot);
        return pack;
    }

    static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    static void PackRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "silksong-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = new byte[TextureFormats.ChainSize(Dxt5, 16, 8, 3)];
            new Random(3).NextBytes(src);
            var entry = DxtEntry(TextureFormats.DXT5, 16, 8, 3, src);
            var blob = TextureTranscode.Transcode(entry, src);
            var patch = new TexturePatch
            {
                AssetFile = "CAB-1", PathId = 7, Name = "atlas", Width = 16, Height = 8, MipCount = 3,
                SourceFormat = TextureFormats.DXT5, TargetFormat = TextureFormats.ETC2_RGBA8,
                Location = "stream", StreamPath = "archive:/CAB-1/CAB-1.resS", StreamOffset = 128, Size = src.Length,
                SourceSha256 = Sha(src), BlobSha256 = Sha(blob),
            };
            var manifest = new TexturePatchManifest { FileCount = 1, TextureCount = 1, SourceBytes = src.Length, Etc2Bytes = src.Length };
            manifest.Files["aa/x.bundle"] = new List<TexturePatch> { patch };
            string pack = MakePack(dir, manifest, new() { [patch.BlobSha256] = blob });
            Eq(0, TextureTranscode.Audit(pack), "audit passes");
            using (var opened = new TextureTranscode.Pack(pack))
            {
                Eq(1, opened.Manifest.Files["aa/x.bundle"].Count, "manifest round trip");
                Eq(TextureTranscode.Contract, opened.Manifest.Contract, "contract");
                True(opened.Blob(patch.BlobSha256).AsSpan().SequenceEqual(blob), "blob round trip");
                True(opened.ManifestSha256.Length == 64, "manifest digest");
            }
            // Same inputs, same pack bytes: the manifest hash is stable.
            string again = MakePack(Path.Combine(dir, "b"), manifest, new() { [patch.BlobSha256] = blob });
            using (var a = new TextureTranscode.Pack(pack)) using (var b = new TextureTranscode.Pack(again))
                Eq(a.ManifestSha256, b.ManifestSha256, "deterministic manifest");

            // A wrong-size record fails the audit.
            var bad = new TexturePatchManifest { FileCount = 1, TextureCount = 1 };
            bad.Files["aa/x.bundle"] = new List<TexturePatch> { new TexturePatch
            {
                AssetFile = "CAB-1", PathId = 7, Width = 16, Height = 8, MipCount = 3, SourceFormat = TextureFormats.DXT5,
                TargetFormat = TextureFormats.ETC2_RGBA8, Location = "stream", Size = src.Length - 16, BlobSha256 = Sha(blob), SourceSha256 = Sha(src),
            } };
            string badPack = MakePack(Path.Combine(dir, "c"), bad, new() { [Sha(blob)] = blob });
            bool threw = false;
            try { TextureTranscode.Audit(badPack); } catch (InvalidDataException) { threw = true; }
            True(threw, "size mismatch rejected");
            // A tampered blob fails its digest.
            var tampered = (byte[])blob.Clone(); tampered[5] ^= 0xFF;
            string tamperedPack = MakePack(Path.Combine(dir, "d"), manifest, new() { [patch.BlobSha256] = tampered });
            threw = false;
            try { TextureTranscode.Audit(tamperedPack); } catch (InvalidDataException) { threw = true; }
            True(threw, "tampered blob rejected");
            // A wrong contract is refused at open.
            var foreign = new TexturePatchManifest { Contract = "something-else", FileCount = 0, TextureCount = 0 };
            string foreignPack = MakePack(Path.Combine(dir, "e"), foreign, new());
            threw = false;
            try { using var _ = new TextureTranscode.Pack(foreignPack); } catch (InvalidDataException) { threw = true; }
            True(threw, "foreign contract rejected");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A fake field tree is not available without AssetsTools; the range arithmetic is what matters here.</summary>
    static void RangePatch()
    {
        var resource = new byte[1024];
        new Random(8).NextBytes(resource);
        var before = (byte[])resource.Clone();
        var blob = new byte[256];
        new Random(9).NextBytes(blob);
        blob.CopyTo(resource.AsSpan(512, 256));
        for (int i = 0; i < 1024; i++)
        {
            if (i >= 512 && i < 768) Eq(blob[i - 512], resource[i], $"patched byte {i}");
            else Eq(before[i], resource[i], $"untouched byte {i}");
        }
    }

    static void ApplySerialized()
    {
        string? fixture = Environment.GetEnvironmentVariable("SILKSONG_TEXTURE_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture)) throw new SkipException("SILKSONG_TEXTURE_FIXTURE not set");
        if (!File.Exists(fixture)) throw new SkipException("fixture is not a single serialized file: " + fixture);
        string dir = Path.Combine(Path.GetTempPath(), "silksong-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "root"));
        try
        {
            string classData = Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
            string copy = Path.Combine(dir, "root", Path.GetFileName(fixture));
            File.Copy(fixture, copy);
            // Find an inline block-compressed texture to swap for same-size bytes.
            var entries = new System.Collections.Concurrent.ConcurrentBag<TextureEntry>();
            var others = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            var payloads = new Dictionary<(string, long), byte[]>();
            TextureReport.InspectSerialized(copy, Path.GetFileName(fixture), true, classData, entries, others, payloads);
            var target = entries.FirstOrDefault(e => e.DataLocation == "inline" && e.PayloadBytes >= 64 && e.PayloadMatchesExpected
                && TextureFormats.Describe(e.FormatId).IsBlockCompressed);
            if (target == null) throw new SkipException("fixture has no inline block-compressed texture");
            var src = payloads[(target.AssetFile, target.PathId)];
            var blob = (byte[])src.Clone();
            for (int i = 0; i < blob.Length; i++) blob[i] ^= 0x5A;
            int targetFormat = target.FormatId == TextureFormats.ETC2_RGB ? TextureFormats.ETC2_RGBA1 : TextureFormats.ETC2_RGB;
            var patch = new TexturePatch
            {
                AssetFile = target.AssetFile, PathId = target.PathId, Name = target.Name, Width = target.Width, Height = target.Height,
                MipCount = target.MipCount, SourceFormat = target.FormatId, TargetFormat = targetFormat, Location = "inline",
                Size = src.Length, SourceSha256 = Sha(src), BlobSha256 = Sha(blob),
            };
            var manifest = new TexturePatchManifest { FileCount = 1, TextureCount = 1 };
            manifest.Files[Path.GetFileName(fixture)] = new List<TexturePatch> { patch };
            string pack = MakePack(dir, manifest, new() { [patch.BlobSha256] = blob });

            Eq(0, TextureTranscode.ApplyToSerialized(Path.Combine(dir, "root"), pack, classData), "apply");
            // Parse back independently of the verifier.
            var after = new System.Collections.Concurrent.ConcurrentBag<TextureEntry>();
            var afterPayloads = new Dictionary<(string, long), byte[]>();
            TextureReport.InspectSerialized(copy, Path.GetFileName(fixture), true, classData, after, others, afterPayloads);
            var changed = after.Single(e => e.PathId == target.PathId);
            Eq(targetFormat, changed.FormatId, "format flipped");
            True(afterPayloads[(target.AssetFile, target.PathId)].AsSpan().SequenceEqual(blob), "bytes swapped");
            Eq(after.Count, entries.Count, "no asset lost");
            // Every other texture is byte-identical.
            foreach (var e in entries.Where(e => e.PathId != target.PathId && payloads.ContainsKey((e.AssetFile, e.PathId))))
                True(afterPayloads[(e.AssetFile, e.PathId)].AsSpan().SequenceEqual(payloads[(e.AssetFile, e.PathId)]), $"untouched {e.Name}");
            // Second run: already applied, nothing rewritten.
            long mtime = new FileInfo(copy).LastWriteTimeUtc.Ticks;
            Eq(0, TextureTranscode.ApplyToSerialized(Path.Combine(dir, "root"), pack, classData), "reapply");
            Eq(mtime, new FileInfo(copy).LastWriteTimeUtc.Ticks, "idempotent: file untouched");
            Console.WriteLine($"      applied to {target.Name} ({target.Format} {target.Width}x{target.Height}, {src.Length} bytes)");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── BC7 ─────────────────────────────────────────────────────────────────

    sealed class BitWriter
    {
        public readonly byte[] Bytes = new byte[16];
        int _at;
        public void Write(int value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                if (((value >> i) & 1) != 0) Bytes[_at >> 3] |= (byte)(1 << (_at & 7));
                _at++;
            }
        }
    }

    static void Bc7Solid()
    {
        // Mode 6: 7-bit endpoints + p-bit, 4-bit indices. Both endpoints the
        // same, p = 1: colour 0x55 -> 0xAB, alpha 0x7F -> 0xFF.
        var w = new BitWriter();
        w.Write(1 << 6, 7);                 // six zeros then a one
        for (int e = 0; e < 2; e++) w.Write(0x55, 7); // R0 R1
        for (int e = 0; e < 2; e++) w.Write(0x2A, 7); // G0 G1 -> 0x55
        for (int e = 0; e < 2; e++) w.Write(0x00, 7); // B0 B1 -> 0x01
        for (int e = 0; e < 2; e++) w.Write(0x7F, 7); // A0 A1 -> 0xFF
        w.Write(1, 1); w.Write(1, 1);       // p-bits
        w.Write(0, 3); for (int p = 1; p < 16; p++) w.Write(0, 4);
        var rgba = new byte[64];
        Bc7.DecodeBlock(w.Bytes, rgba);
        for (int p = 0; p < 16; p++)
        {
            Eq((byte)0xAB, rgba[p * 4], $"r {p}"); Eq((byte)0x55, rgba[p * 4 + 1], $"g {p}");
            Eq((byte)0x01, rgba[p * 4 + 2], $"b {p}"); Eq((byte)0xFF, rgba[p * 4 + 3], $"a {p}");
        }
        // Indices at the far endpoint select endpoint 1: make R1 differ.
        var w2 = new BitWriter();
        w2.Write(1 << 6, 7);
        w2.Write(0x00, 7); w2.Write(0x7F, 7);   // R0 = 0, R1 = 0xFF (with p)
        w2.Write(0, 7); w2.Write(0, 7); w2.Write(0, 7); w2.Write(0, 7);
        w2.Write(0x7F, 7); w2.Write(0x7F, 7);
        w2.Write(1, 1); w2.Write(1, 1);
        w2.Write(0, 3);                          // pixel 0 -> endpoint 0
        for (int p = 1; p < 16; p++) w2.Write(15, 4); // the rest -> endpoint 1
        Bc7.DecodeBlock(w2.Bytes, rgba);
        Eq((byte)1, rgba[0], "pixel 0 red at e0 (p-bit)");
        Eq((byte)255, rgba[4], "pixel 1 red at e1");
        // Weight 8 of 15: (64-34)*1 + 34*255 + 32 >> 6 = 136.
        var w3 = new BitWriter();
        w3.Write(1 << 6, 7);
        w3.Write(0x00, 7); w3.Write(0x7F, 7);
        w3.Write(0, 7); w3.Write(0, 7); w3.Write(0, 7); w3.Write(0, 7);
        w3.Write(0x7F, 7); w3.Write(0x7F, 7);
        w3.Write(1, 1); w3.Write(1, 1);
        w3.Write(0, 3); for (int p = 1; p < 16; p++) w3.Write(8, 4);
        Bc7.DecodeBlock(w3.Bytes, rgba);
        Eq((byte)136, rgba[4], "interpolated red");
    }

    static void Bc7Rotation()
    {
        // Mode 5: rotation 1 swaps alpha and red. Endpoints: R = 0, A = 0xFF.
        var w = new BitWriter();
        w.Write(1 << 5, 6);      // five zeros then a one
        w.Write(1, 2);           // rotation: A <-> R
        w.Write(0, 7); w.Write(0, 7);        // R0 R1
        w.Write(0x40, 7); w.Write(0x40, 7);  // G -> 0x81
        w.Write(0, 7); w.Write(0, 7);        // B
        w.Write(0xFF, 8); w.Write(0xFF, 8);  // A0 A1
        w.Write(0, 1); for (int p = 1; p < 16; p++) w.Write(0, 2);   // colour indices
        w.Write(0, 1); for (int p = 1; p < 16; p++) w.Write(0, 2);   // alpha indices
        var rgba = new byte[64];
        Bc7.DecodeBlock(w.Bytes, rgba);
        Eq((byte)255, rgba[0], "red took alpha's value");
        Eq((byte)0, rgba[3], "alpha took red's value");
        Eq((byte)0x81, rgba[1], "green expanded from 7 bits");
    }

    static void Bc7Reserved()
    {
        var rgba = new byte[64];
        Array.Fill(rgba, (byte)9);
        Bc7.DecodeBlock(new byte[16], rgba);
        for (int i = 0; i < 64; i++) Eq((byte)0, rgba[i], $"byte {i}");
    }

    static void Bc7Level()
    {
        var payload = new byte[2 * 1 * 16];
        payload[0] = 1 << 6; // mode 6, everything else zero -> black opaque? alpha endpoints 0 -> 0
        payload[16] = 1 << 6;
        var rgba = Bc7.DecodeLevel(payload, 5, 3);
        Eq(5 * 3 * 4, rgba.Length, "clipped size");
        bool threw = false;
        try { Bc7.DecodeLevel(new byte[16], 8, 4); } catch (InvalidDataException) { threw = true; }
        True(threw, "8x4 needs 32 bytes");
    }

    static void Bc7Transcode()
    {
        var rng = new Random(17);
        int w = 24, h = 40, mips = 4;
        var bc7 = TextureFormats.Describe(TextureFormats.BC7);
        var payload = new byte[TextureFormats.ChainSize(bc7, w, h, mips)];
        rng.NextBytes(payload);
        var entry = Entry("bc7", TextureFormats.BC7, w, h, mips, payload: payload.Length);
        entry.SuggestedTargetId = TextureFormats.SameSizeEtc2Target(bc7, false)!.Id;
        Eq(TextureFormats.ETC2_RGBA8, entry.SuggestedTargetId!.Value, "target");
        Eq(null, entry.ConversionBlocker, "convertible");
        var etc = TextureTranscode.Transcode(entry, payload);
        Eq(payload.Length, etc.Length, "same length");
        var back = Etc2.DecodeLevel(TextureFormats.ETC2_RGBA8, etc.AsSpan(0, (int)TextureFormats.LevelSize(Etc2Rgba8, w, h)), w, h);
        Eq(w * h * 4, back.Length, "level 0 decodes");
    }

    static void Bc7Dump()
    {
        string? dir = Environment.GetEnvironmentVariable("SILKSONG_BC7_DUMP");
        if (string.IsNullOrWhiteSpace(dir)) throw new SkipException("SILKSONG_BC7_DUMP not set");
        Directory.CreateDirectory(dir);
        // 256x256 of random blocks: 4096 blocks across every mode, partition,
        // rotation and index pattern. Reserved-mode blocks included.
        int w = 256, h = 256;
        var payload = new byte[w * h];
        new Random(99).NextBytes(payload);
        File.WriteAllBytes(Path.Combine(dir, "bc7.bin"), payload);
        File.WriteAllBytes(Path.Combine(dir, "bc7-decoded.rgba"), Bc7.DecodeLevel(payload, w, h));
        File.WriteAllText(Path.Combine(dir, "dims.txt"), $"{w} {h}\n");
    }
}
