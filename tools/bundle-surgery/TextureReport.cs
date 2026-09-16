// TextureReport — what the game's textures would cost on an Android GPU.
//
//   texture-report <root> <out.json> [--skip-payload-scan]
//
// Walks every Unity serialized file and AssetBundle under <root> (a depot's
// *_Data directory, or any tree containing them), records every Texture2D it
// finds, and writes one JSON document plus a console summary. It opens files
// for reading only and never writes anything under <root>.
//
// The question this answers is narrow and worth stating precisely. Desktop
// block-compressed formats -- DXT1, DXT5, BC7 -- are not sampled by any
// Android GPU this port runs on, so the player expands them to RGBA32 at load
// time (see TextureFormats.cs). The report says how much of the shipped
// content is in such formats, what its expansion would cost, and what a
// same-size ETC2 swap would save. It is a CAPACITY figure over everything the
// game ships. It does not know which textures the language-select screen has
// resident; the runtime probe in the patch assembly (TextureFormatProbe)
// answers that, from the device.
//
// Reading the payload is optional and on by default. It is what tells a DXT1
// texture that needs ETC2_RGBA1 (uses one-bit transparency) from one that can
// be ETC2_RGB, and it proves each streamed range actually exists in its .resS
// file. On a multi-gigabyte depot that is a lot of I/O, so it can be skipped.

using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BundleSurgery;

/// <summary>One Texture2D, as found. Every size is in bytes.</summary>
internal sealed class TextureEntry
{
    /// <summary>The container, relative to the report root, with forward slashes.</summary>
    public string File { get; set; } = "";
    /// <summary>"bundle" or "serialized".</summary>
    public string Container { get; set; } = "";
    /// <summary>The serialized file the asset lives in: the bundle's inner CAB name, or the file itself.</summary>
    public string AssetFile { get; set; } = "";
    public long PathId { get; set; }
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Depth { get; set; } = 1;
    public int Dimension { get; set; }
    public int FormatId { get; set; }
    public string Format { get; set; } = "";
    public string Family { get; set; } = "";
    public int MipCount { get; set; }
    public int MipsStripped { get; set; }
    public int ImageCount { get; set; }
    public bool Readable { get; set; }
    public int ColorSpace { get; set; }
    public bool StreamingMipmaps { get; set; }
    /// <summary>"inline", "stream" or "none".</summary>
    public string DataLocation { get; set; } = "none";
    public string? StreamPath { get; set; }
    public long StreamOffset { get; set; }
    public long StreamSize { get; set; }
    public long CompleteImageSize { get; set; }
    /// <summary>Bytes actually present: the inline array or the stream range.</summary>
    public long PayloadBytes { get; set; }
    /// <summary>Bytes the dimensions and format imply, or -1 when the format's size is not predictable.</summary>
    public long ExpectedBytes { get; set; } = -1;
    public bool PayloadMatchesExpected { get; set; }
    /// <summary>The payload could be located (and, if scanned, read in full).</summary>
    public bool PayloadReadable { get; set; }
    public string? PayloadIssue { get; set; }
    /// <summary>What this texture costs once loaded on Android: RGBA32 for desktop-only formats.</summary>
    public long AndroidResidentBytes { get; set; } = -1;
    /// <summary>Its cost if swapped to the suggested ETC2 target, or -1 when there is no same-size target.</summary>
    public long Etc2Bytes { get; set; } = -1;
    public int? SuggestedTargetId { get; set; }
    public string? SuggestedTarget { get; set; }
    /// <summary>Null when the payload was not scanned.</summary>
    public bool? Dxt1PunchThrough { get; set; }
    public int Dxt1PunchThroughBlocks { get; set; }
    /// <summary>Resident minus ETC2, for textures the same-size swap could handle. Zero otherwise.</summary>
    public long EstimatedSavingBytes { get; set; }
    /// <summary>Why the same-size swap does not apply, or null when it does.</summary>
    public string? ConversionBlocker { get; set; }
    /// <summary>For blocked desktop-only textures: what a native-size replacement would still save.</summary>
    public long BlockedSavingBytes { get; set; }
}

internal sealed class FormatSummary
{
    public string Format { get; set; } = "";
    public string Family { get; set; } = "";
    public int FormatId { get; set; }
    public int Count { get; set; }
    public long SourceBytes { get; set; }
    public long AndroidResidentBytes { get; set; }
    public long Etc2Bytes { get; set; }
    public long EstimatedSavingBytes { get; set; }
    public int Convertible { get; set; }
    public int Blocked { get; set; }
}

internal sealed class ReportTotals
{
    public int Textures { get; set; }
    public int DesktopOnlyTextures { get; set; }
    public long SourceBytes { get; set; }
    public long AndroidResidentBytes { get; set; }
    public long Etc2Bytes { get; set; }
    public long EstimatedSavingBytes { get; set; }
    public long BlockedSavingBytes { get; set; }
    public int Convertible { get; set; }
    public int Blocked { get; set; }
    public int PayloadMismatches { get; set; }
    public int PayloadUnreadable { get; set; }
}

internal sealed class TextureReportDocument
{
    public string Scope { get; set; } =
        "Total texture capacity of the content under root. NOT the set of textures resident in any scene.";
    public string GeneratedUtc { get; set; } = "";
    public string Root { get; set; } = "";
    public bool PayloadsScanned { get; set; }
    public int Bundles { get; set; }
    public int SerializedFiles { get; set; }
    public int FilesFailed { get; set; }
    public Dictionary<string, int> OtherTextureClasses { get; set; } = new();
    public ReportTotals Totals { get; set; } = new();
    public List<FormatSummary> ByFormat { get; set; } = new();
    public Dictionary<string, int> Blockers { get; set; } = new();
    public List<TextureEntry> Textures { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

internal static class TextureReport
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    const int TargetOnly2D = TextureFormats.TextureDimension2D;

    public static int Run(string root, string outputPath, bool scanPayloads, string classDataPath)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"  ✗ not a directory: {root}");
            return 1;
        }

        var bundles = Directory.GetFiles(root, "*.bundle", SearchOption.AllDirectories);
        var serialized = FindSerializedFiles(root);
        var inputs = bundles.Select(p => (path: p, bundle: true))
            .Concat(serialized.Select(p => (path: p, bundle: false)))
            .OrderBy(x => x.path, StringComparer.Ordinal)
            .ToArray();
        if (inputs.Length == 0)
        {
            Console.Error.WriteLine($"  ✗ no bundles or serialized files under {root}");
            return 1;
        }
        Console.WriteLine($"  texture report: {bundles.Length} bundle(s), {serialized.Length} serialized file(s) under {root}");
        Console.WriteLine($"  payload scan: {(scanPayloads ? "on" : "off")}");

        var entries = new ConcurrentBag<TextureEntry>();
        var others = new ConcurrentDictionary<string, int>();
        var errors = new ConcurrentBag<string>();
        int processed = 0, failed = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Parallel.ForEach(inputs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, input =>
        {
            string relative = Path.GetRelativePath(root, input.path).Replace('\\', '/');
            try
            {
                if (input.bundle) InspectBundle(input.path, relative, scanPayloads, classDataPath, entries, others);
                else InspectSerialized(input.path, relative, scanPayloads, classDataPath, entries, others);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref failed);
                errors.Add($"{relative}: {e.Message}");
            }
            int n = Interlocked.Increment(ref processed);
            if (n % 100 == 0 || n == inputs.Length)
            {
                Console.WriteLine($"  {n} / {inputs.Length}  ({stopwatch.Elapsed.TotalSeconds:N0}s)");
                Console.Out.Flush();
            }
        });

        var document = Summarize(entries, root, scanPayloads);
        document.Bundles = bundles.Length;
        document.SerializedFiles = serialized.Length;
        document.FilesFailed = failed;
        foreach (var pair in others) document.OtherTextureClasses[pair.Key] = pair.Value;
        document.Errors = errors.OrderBy(x => x, StringComparer.Ordinal).ToList();

        string full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, document, JsonOptions);
        File.Move(temp, full, overwrite: true);

        PrintSummary(document, Console.Out);
        Console.WriteLine($"  JSON: {full}");
        foreach (string error in document.Errors.Take(10)) Console.Error.WriteLine($"    ✗ {error}");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Serialized files are found by their header rather than by name, since
    /// the depot's are named globalgamemanagers, level0, sharedassets0.assets,
    /// resources.assets and "unity default resources" with no one pattern.
    /// The header's third big-endian uint is the format version, which has
    /// been a small number for every Unity release and stays one in the
    /// large-file layout (version 22+), where the sizes move but the version
    /// field does not.
    /// </summary>
    internal static string[] FindSerializedFiles(string root)
    {
        var found = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".bundle" or ".ress" or ".resource" or ".dll" or ".json" or ".txt" or ".dat" or ".so" or ".config" or ".info" or ".bin" or ".xml" or ".pdb" or ".png" or ".ogg" or ".wav" or ".bank")
                continue;
            if (LooksLikeSerializedFile(path)) found.Add(path);
        }
        return found.ToArray();
    }

    internal static bool LooksLikeSerializedFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 20) return false;
            Span<byte> header = stackalloc byte[20];
            if (stream.Read(header) != header.Length) return false;
            if (header.StartsWith("UnityFS"u8)) return false;
            uint version = (uint)((header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11]);
            return version >= 5 && version <= 64;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static void CountOthers(AssetsFileInstance afile, ConcurrentDictionary<string, int> others)
    {
        foreach (var (id, name) in new[]
        {
            (AssetClassID.Cubemap, "Cubemap"),
            (AssetClassID.Texture2DArray, "Texture2DArray"),
            (AssetClassID.Texture3D, "Texture3D"),
            (AssetClassID.CubemapArray, "CubemapArray"),
        })
        {
            int n = afile.file.GetAssetsOfType(id).Count;
            if (n > 0) others.AddOrUpdate(name, n, (_, old) => old + n);
        }
    }

    static void InspectBundle(
        string path, string relative, bool scanPayloads, string classDataPath,
        ConcurrentBag<TextureEntry> entries, ConcurrentDictionary<string, int> others)
    {
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDataPath);
        try
        {
            var bundle = manager.LoadBundleFile(path, true);
            manager.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
            var directory = bundle.file.BlockAndDirInfo.DirectoryInfos;
            foreach (var dirInfo in directory)
            {
                if ((dirInfo.Flags & 4) == 0) continue;
                var afile = manager.LoadAssetsFileFromBundle(bundle, dirInfo.Name);
                CountOthers(afile, others);
                foreach (var asset in afile.file.GetAssetsOfType(AssetClassID.Texture2D))
                {
                    var bf = manager.GetBaseField(afile, asset);
                    if (bf == null) continue;
                    var entry = Describe(bf, relative, "bundle", dirInfo.Name, asset.PathId);
                    ResolvePayload(entry, bf, scanPayloads, streamPath =>
                    {
                        // "archive:/CAB-xxxx/CAB-xxxx.resS": the resource file is a
                        // sibling entry of the bundle, addressed by its last segment.
                        string name = streamPath.Substring(streamPath.LastIndexOf('/') + 1);
                        var res = directory.FirstOrDefault(d => d.Name == name);
                        if (res == null) return null;
                        return new PayloadSource(res.DecompressedSize, (offset, size) =>
                        {
                            var reader = bundle.file.DataReader;
                            reader.Position = res.Offset + offset;
                            return reader.ReadBytes(checked((int)size));
                        });
                    });
                    entries.Add(entry);
                }
            }
        }
        finally { manager.UnloadAll(); }
    }

    static void InspectSerialized(
        string path, string relative, bool scanPayloads, string classDataPath,
        ConcurrentBag<TextureEntry> entries, ConcurrentDictionary<string, int> others)
    {
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDataPath);
        try
        {
            var afile = manager.LoadAssetsFile(path, false);
            manager.LoadClassDatabaseFromPackage(afile.file.Metadata.UnityVersion);
            CountOthers(afile, others);
            string directory = Path.GetDirectoryName(path)!;
            foreach (var asset in afile.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                var bf = manager.GetBaseField(afile, asset);
                if (bf == null) continue;
                var entry = Describe(bf, relative, "serialized", Path.GetFileName(path), asset.PathId);
                ResolvePayload(entry, bf, scanPayloads, streamPath =>
                {
                    // A plain player file names its .resS relative to itself.
                    string candidate = Path.Combine(directory, streamPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!System.IO.File.Exists(candidate)) return null;
                    long length = new FileInfo(candidate).Length;
                    return new PayloadSource(length, (offset, size) =>
                    {
                        using var stream = System.IO.File.OpenRead(candidate);
                        stream.Position = offset;
                        var bytes = new byte[size];
                        int read = 0;
                        while (read < bytes.Length)
                        {
                            int n = stream.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) throw new EndOfStreamException($"{streamPath} ended {bytes.Length - read} bytes early");
                            read += n;
                        }
                        return bytes;
                    });
                });
                entries.Add(entry);
            }
        }
        finally { manager.UnloadAll(); }
    }

    internal sealed record PayloadSource(long Length, Func<long, long, byte[]> Read);

    static AssetTypeValueField? Field(AssetTypeValueField parent, string name)
    {
        var f = parent[name];
        return f == null || f.IsDummy ? null : f;
    }

    static int Int(AssetTypeValueField parent, string name, int fallback = 0) =>
        Field(parent, name) is { } f ? f.AsInt : fallback;

    static bool Bool(AssetTypeValueField parent, string name) =>
        Field(parent, name) is { } f && f.AsBool;

    /// <summary>The serialized fields, with nothing derived yet.</summary>
    internal static TextureEntry Describe(AssetTypeValueField bf, string file, string container, string assetFile, long pathId)
    {
        var entry = new TextureEntry
        {
            File = file,
            Container = container,
            AssetFile = assetFile,
            PathId = pathId,
            Name = Field(bf, "m_Name")?.AsString ?? "",
            Width = Int(bf, "m_Width"),
            Height = Int(bf, "m_Height"),
            Dimension = Int(bf, "m_TextureDimension", TargetOnly2D),
            FormatId = Int(bf, "m_TextureFormat"),
            MipCount = Int(bf, "m_MipCount", 1),
            MipsStripped = Int(bf, "m_MipsStripped"),
            ImageCount = Int(bf, "m_ImageCount", 1),
            Readable = Bool(bf, "m_IsReadable"),
            ColorSpace = Int(bf, "m_ColorSpace"),
            StreamingMipmaps = Bool(bf, "m_StreamingMipmaps"),
            CompleteImageSize = Field(bf, "m_CompleteImageSize") is { } cis ? cis.AsLong : 0,
        };
        var format = TextureFormats.Describe(entry.FormatId);
        entry.Format = format.Name;
        entry.Family = format.Family.ToString();

        var image = Field(bf, "image data");
        long inline = image?.AsByteArray?.LongLength ?? 0;
        var stream = Field(bf, "m_StreamData");
        if (stream != null)
        {
            entry.StreamPath = Field(stream, "path")?.AsString;
            entry.StreamOffset = Field(stream, "offset") is { } off ? (long)off.AsULong : 0;
            entry.StreamSize = Field(stream, "size") is { } size ? size.AsUInt : 0;
        }
        if (entry.StreamSize > 0 && !string.IsNullOrEmpty(entry.StreamPath))
        {
            entry.DataLocation = "stream";
            entry.PayloadBytes = entry.StreamSize;
        }
        else if (inline > 0)
        {
            entry.DataLocation = "inline";
            entry.PayloadBytes = inline;
            entry.StreamPath = null;
        }
        else
        {
            entry.DataLocation = "none";
            entry.PayloadBytes = 0;
        }
        return entry;
    }

    /// <summary>
    /// Everything derived: expected sizes, Android cost, ETC2 target and the
    /// saving. Reads the payload when asked, through <paramref name="locate"/>
    /// for streamed data.
    /// </summary>
    internal static void ResolvePayload(
        TextureEntry entry, AssetTypeValueField? bf, bool scanPayloads, Func<string, PayloadSource?> locate)
    {
        var format = TextureFormats.Describe(entry.FormatId);
        int images = Math.Max(1, entry.ImageCount);
        long chain = TextureFormats.ChainSize(format, entry.Width, entry.Height, entry.MipCount);
        entry.ExpectedBytes = chain < 0 ? -1 : chain * images;
        entry.PayloadMatchesExpected = entry.ExpectedBytes >= 0 && entry.PayloadBytes == entry.ExpectedBytes;

        long resident = TextureFormats.AndroidResidentSize(format, entry.Width, entry.Height, entry.MipCount);
        entry.AndroidResidentBytes = resident < 0 ? -1 : resident * images;

        entry.ConversionBlocker = TextureFormats.ConversionBlocker(format, entry.Dimension, entry.ImageCount);
        if (entry.ConversionBlocker == null && entry.DataLocation == "none") entry.ConversionBlocker = "no-payload";
        // Nothing to locate: an empty texture is complete as it is, and one
        // that should have bytes but has none is a finding, not an I/O error.
        if (entry.DataLocation == "none")
        {
            entry.PayloadReadable = entry.ExpectedBytes == 0;
            if (!entry.PayloadReadable) entry.PayloadIssue = "no payload";
        }
        if (entry.ConversionBlocker == null && !entry.PayloadMatchesExpected) entry.ConversionBlocker = "payload-size-mismatch";

        // Locate, and optionally read, the bytes.
        byte[]? payload = null;
        try
        {
            if (entry.DataLocation == "inline")
            {
                entry.PayloadReadable = true;
                if (scanPayloads && bf != null) payload = Field(bf, "image data")?.AsByteArray;
            }
            else if (entry.DataLocation == "stream")
            {
                var source = locate(entry.StreamPath!);
                if (source == null)
                {
                    entry.PayloadIssue = $"stream file not found: {entry.StreamPath}";
                }
                else if (entry.StreamOffset < 0 || entry.StreamOffset + entry.StreamSize > source.Length)
                {
                    entry.PayloadIssue =
                        $"stream range {entry.StreamOffset}+{entry.StreamSize} exceeds {entry.StreamPath} ({source.Length} bytes)";
                }
                else
                {
                    entry.PayloadReadable = true;
                    if (scanPayloads) payload = source.Read(entry.StreamOffset, entry.StreamSize);
                }
            }
        }
        catch (Exception e)
        {
            entry.PayloadReadable = false;
            entry.PayloadIssue = e.Message;
        }
        if (entry.PayloadIssue != null && entry.ConversionBlocker == null) entry.ConversionBlocker = "payload-unreadable";

        bool punch = false;
        if (format.Family == TextureFamily.Dxt1 && payload != null && payload.Length % Dxt.Dxt1BlockBytes == 0)
        {
            entry.Dxt1PunchThroughBlocks = Dxt.CountDxt1PunchThroughBlocks(payload);
            entry.Dxt1PunchThrough = entry.Dxt1PunchThroughBlocks > 0;
            punch = entry.Dxt1PunchThrough.Value;
        }

        var target = TextureFormats.SameSizeEtc2Target(format, punch);
        if (target != null)
        {
            entry.SuggestedTargetId = target.Id;
            entry.SuggestedTarget = target.Name
                + (format.Family == TextureFamily.Dxt1 && entry.Dxt1PunchThrough == null ? " (unscanned; ETC2_RGBA1 if punch-through)" : "");
            entry.Etc2Bytes = TextureFormats.ChainSize(target, entry.Width, entry.Height, entry.MipCount) * images;
        }

        if (entry.ConversionBlocker == null && entry.Etc2Bytes >= 0 && entry.AndroidResidentBytes >= 0)
            entry.EstimatedSavingBytes = Math.Max(0, entry.AndroidResidentBytes - entry.Etc2Bytes);
        else if (format.IsDesktopOnly && entry.AndroidResidentBytes >= 0)
        {
            // What a native-size replacement of the same block size would
            // still save, once something can produce one.
            long native = TextureFormats.ChainSize(TextureFormats.Decoded(format), entry.Width, entry.Height, entry.MipCount);
            if (native >= 0) entry.BlockedSavingBytes = Math.Max(0, entry.AndroidResidentBytes - native * images);
        }
    }

    /// <summary>Pure: totals and groupings from entries alone.</summary>
    internal static TextureReportDocument Summarize(IEnumerable<TextureEntry> entries, string root, bool payloadsScanned)
    {
        var document = new TextureReportDocument
        {
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            Root = root,
            PayloadsScanned = payloadsScanned,
            Textures = entries
                .OrderBy(e => e.File, StringComparer.Ordinal)
                .ThenBy(e => e.AssetFile, StringComparer.Ordinal)
                .ThenBy(e => e.PathId)
                .ToList(),
        };
        var totals = document.Totals;
        foreach (var e in document.Textures)
        {
            var format = TextureFormats.Describe(e.FormatId);
            totals.Textures++;
            if (format.IsDesktopOnly) totals.DesktopOnlyTextures++;
            totals.SourceBytes += Math.Max(0, e.PayloadBytes);
            if (e.AndroidResidentBytes > 0) totals.AndroidResidentBytes += e.AndroidResidentBytes;
            if (e.ConversionBlocker == null)
            {
                totals.Convertible++;
                totals.Etc2Bytes += Math.Max(0, e.Etc2Bytes);
                totals.EstimatedSavingBytes += e.EstimatedSavingBytes;
            }
            else
            {
                totals.Blocked++;
                totals.BlockedSavingBytes += e.BlockedSavingBytes;
                document.Blockers.TryGetValue(e.ConversionBlocker, out int n);
                document.Blockers[e.ConversionBlocker] = n + 1;
            }
            if (e.ExpectedBytes >= 0 && !e.PayloadMatchesExpected) totals.PayloadMismatches++;
            if (!e.PayloadReadable) totals.PayloadUnreadable++;
        }
        document.ByFormat = document.Textures
            .GroupBy(e => e.FormatId)
            .Select(g => new FormatSummary
            {
                FormatId = g.Key,
                Format = TextureFormats.Describe(g.Key).Name,
                Family = TextureFormats.Describe(g.Key).Family.ToString(),
                Count = g.Count(),
                SourceBytes = g.Sum(e => Math.Max(0, e.PayloadBytes)),
                AndroidResidentBytes = g.Sum(e => Math.Max(0, e.AndroidResidentBytes)),
                Etc2Bytes = g.Where(e => e.ConversionBlocker == null).Sum(e => Math.Max(0, e.Etc2Bytes)),
                EstimatedSavingBytes = g.Sum(e => e.EstimatedSavingBytes),
                Convertible = g.Count(e => e.ConversionBlocker == null),
                Blocked = g.Count(e => e.ConversionBlocker != null),
            })
            .OrderByDescending(s => s.AndroidResidentBytes)
            .ThenBy(s => s.FormatId)
            .ToList();
        document.Blockers = document.Blockers.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
        return document;
    }

    static string MiB(long bytes) => bytes < 0 ? "n/a" : $"{bytes / 1048576.0:N1} MiB";

    internal static void PrintSummary(TextureReportDocument d, TextWriter o)
    {
        o.WriteLine();
        o.WriteLine("  ── Texture capacity report ──────────────────────────────────────────");
        o.WriteLine($"  {d.Scope}");
        o.WriteLine($"  {d.Bundles} bundle(s), {d.SerializedFiles} serialized file(s), {d.FilesFailed} failed to open; payload scan {(d.PayloadsScanned ? "on" : "off")}");
        if (d.OtherTextureClasses.Count > 0)
            o.WriteLine("  other texture classes (not inspected): " +
                string.Join(", ", d.OtherTextureClasses.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")));
        o.WriteLine();
        o.WriteLine($"  {"format",-22} {"count",6} {"source",13} {"on Android",13} {"as ETC2",13} {"saving",13} {"ok/blocked",10}");
        foreach (var s in d.ByFormat)
        {
            o.WriteLine($"  {s.Format,-22} {s.Count,6} {MiB(s.SourceBytes),13} {MiB(s.AndroidResidentBytes),13} " +
                $"{(s.Convertible > 0 ? MiB(s.Etc2Bytes) : "-"),13} {(s.EstimatedSavingBytes > 0 ? MiB(s.EstimatedSavingBytes) : "-"),13} {s.Convertible,4}/{s.Blocked,-5}");
        }
        var t = d.Totals;
        o.WriteLine();
        o.WriteLine($"  textures: {t.Textures} ({t.DesktopOnlyTextures} in desktop-only formats)");
        o.WriteLine($"  source bytes:              {MiB(t.SourceBytes)}");
        o.WriteLine($"  resident on Android now:   {MiB(t.AndroidResidentBytes)}  (desktop-only formats expanded to RGBA32)");
        o.WriteLine($"  same-size ETC2 swap:       {t.Convertible} texture(s) -> {MiB(t.Etc2Bytes)}, theoretical saving {MiB(t.EstimatedSavingBytes)}");
        o.WriteLine($"  not yet convertible:       {t.Blocked} texture(s), a further {MiB(t.BlockedSavingBytes)} if they had native-size replacements");
        if (d.Blockers.Count > 0)
            o.WriteLine("    " + string.Join(", ", d.Blockers.Select(p => $"{p.Key}={p.Value}")));
        o.WriteLine($"  payload size mismatches:   {t.PayloadMismatches};  payloads not located: {t.PayloadUnreadable}");
        o.WriteLine();
        o.WriteLine("  largest by Android cost:");
        foreach (var e in d.Textures.Where(e => e.AndroidResidentBytes > 0).OrderByDescending(e => e.AndroidResidentBytes).Take(15))
        {
            o.WriteLine($"    {MiB(e.AndroidResidentBytes),11}  {e.Width,5}x{e.Height,-5} mips={e.MipCount,-2} {e.Format,-14} " +
                $"{(e.ConversionBlocker ?? "convertible"),-22} {e.Name}  [{e.File}]");
        }
        o.WriteLine("  ─────────────────────────────────────────────────────────────────────");
    }
}
