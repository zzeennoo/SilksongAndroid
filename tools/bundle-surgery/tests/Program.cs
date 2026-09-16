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
        True(TextureFormats.SameSizeEtc2Target(TextureFormats.Describe(TextureFormats.BC7), false) == null, "BC7 has no same-size target");
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
        Eq("bc6h-bc7", TextureFormats.ConversionBlocker(TextureFormats.Describe(TextureFormats.BC7), 2, 1), "BC7");
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
        Eq(2, d.Totals.Convertible, "DXT5 + DXT1");
        Eq(5, d.Totals.Blocked, "bc7, crunched, native, short, empty");
        Eq(0, d.Totals.PayloadUnreadable, "an empty texture is not an unlocated payload");
        Eq(1, d.Blockers["bc6h-bc7"], "bc7 blocker");
        Eq(1, d.Blockers["crunched"], "crunched blocker");
        Eq(2, d.Blockers["not-desktop-only"], "native and empty blockers");
        Eq(1, d.Blockers["payload-size-mismatch"], "short blocker");
        Eq(1, d.Totals.PayloadMismatches, "mismatch counted");
        long atlasA = 64L * 1024 * 1024 - 16L * 1024 * 1024;
        long atlasB = 4L * 1024 * 1024 - 512L * 1024;
        Eq(atlasA + atlasB, d.Totals.EstimatedSavingBytes, "saving over convertible only");
        Eq(16L * 1024 * 1024 + 512L * 1024, d.Totals.Etc2Bytes, "etc2 bytes over convertible only");
        True(d.Totals.BlockedSavingBytes >= 512L * 512 * 4 - 512L * 512, "bc7 saving counted as blocked");
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
}
