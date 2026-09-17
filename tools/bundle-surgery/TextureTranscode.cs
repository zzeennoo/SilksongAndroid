// TextureTranscode — DXT payloads re-encoded as same-size ETC2, delivered as
// a patch pack and applied in place.
//
//   build-texture-patches <root> <out.zip>      PC: encode every convertible
//                                               DXT1/DXT5/BC7 Texture2D under <root>
//   audit-texture-patches <zip>                 prove a pack is whole and consistent
//   apply-texture-patches <root> <zip>          apply to serialized files under <root>
//                                               in place (the PC's player image)
//
// and, inside the existing bundle retargets, `--textures <zip>` applies the
// pack's bundle entries while each bundle is being rewritten anyway.
//
// The shape is the GLES shader path's: the PC does the work that needs a
// codec and produces verified bytes; the device only copies bytes into files
// it is already rewriting. Nothing here encodes on the device.
//
// What makes the swap safe is that it is the same size. A DXT1 mip chain and
// an ETC2_RGB (or ETC2_RGBA1) chain of the same dimensions are byte-for-byte
// the same length, and DXT5, BC7 and ETC2_RGBA8 likewise, so the only fields that
// change are m_TextureFormat and the payload bytes themselves: no offset in a
// .resS moves, no m_StreamData.size changes, no m_CompleteImageSize changes,
// no other asset's position shifts. That is asserted at build time (the
// encoded chain must be exactly the source's length or the texture is
// skipped and reported), checked again by the audit, and checked a third
// time after the device writes the file: the asset is parsed back and its
// format and payload digest compared with the manifest. Any mismatch fails
// the whole file rather than leaving a texture half converted.
//
// Only what the report calls convertible is touched: DXT1, DXT5 and BC7, 2D,
// one image, payload located and of the size its dimensions imply. Crunched
// formats, BC4/BC5/BC6H, cubemaps, arrays and anything whose layout is not
// understood are left as they are and counted in the summary.

using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BundleSurgery;

internal sealed class TexturePatch
{
    public string AssetFile { get; set; } = "";
    public long PathId { get; set; }
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int MipCount { get; set; }
    public int SourceFormat { get; set; }
    public int TargetFormat { get; set; }
    /// <summary>"inline" or "stream".</summary>
    public string Location { get; set; } = "";
    public string? StreamPath { get; set; }
    public long StreamOffset { get; set; }
    public long Size { get; set; }
    public string SourceSha256 { get; set; } = "";
    public string BlobSha256 { get; set; } = "";
}

internal sealed class TexturePatchManifest
{
    public int Format { get; set; } = TextureTranscode.PatchFormat;
    public string Contract { get; set; } = TextureTranscode.Contract;
    public string Encoder { get; set; } = Etc2.EncoderVersion;
    public string TargetFamily { get; set; } = "etc2";
    public int FileCount { get; set; }
    public int TextureCount { get; set; }
    public long SourceBytes { get; set; }
    public long AndroidResidentBytes { get; set; }
    public long Etc2Bytes { get; set; }
    public Dictionary<string, int> SourceFormats { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> TargetFormats { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Skipped { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Relative container path (forward slashes) to its patches. Files with none are absent.</summary>
    public Dictionary<string, List<TexturePatch>> Files { get; set; } = new(StringComparer.Ordinal);
}

internal static class TextureTranscode
{
    internal const int PatchFormat = 1;
    // Part of the cache key on the PC and of the signed manifest. The
    // encoder version is inside it because a different encoder produces
    // different bytes for the same texture.
    // v2: BC7 joined the same-size set (it is 16 bytes a block like
    // ETC2_RGBA8). A v1 pack converted DXT only and is refused.
    internal const string Contract = "dxt-bc7-to-etc2-same-size-v2/" + Etc2.EncoderVersion;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
    static readonly ConcurrentDictionary<string, object> BlobWriteLocks = new(StringComparer.Ordinal);

    static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ── building the pack (PC) ───────────────────────────────────────────────

    public static int BuildPack(string root, string outputPath, string classDataPath)
    {
        root = Path.GetFullPath(root);
        var bundles = Directory.GetFiles(root, "*.bundle", SearchOption.AllDirectories);
        var serialized = TextureReport.FindSerializedFiles(root);
        var inputs = bundles.Select(p => (path: p, bundle: true))
            .Concat(serialized.Select(p => (path: p, bundle: false)))
            .OrderBy(x => x.path, StringComparer.Ordinal)
            .ToArray();
        if (inputs.Length == 0) throw new InvalidDataException($"no bundles or serialized files under {root}");
        Console.WriteLine($"  texture patches: {bundles.Length} bundle(s), {serialized.Length} serialized file(s) under {root}");

        string blobRoot = outputPath + ".blobs";
        Directory.CreateDirectory(blobRoot);
        var records = new ConcurrentDictionary<string, List<TexturePatch>>(StringComparer.Ordinal);
        var skipped = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var sourceFormats = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var targetFormats = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        long sourceBytes = 0, residentBytes = 0;
        int processed = 0, textures = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Parallel.ForEach(inputs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, input =>
        {
            string relative = Path.GetRelativePath(root, input.path).Replace('\\', '/');
            var patches = new List<TexturePatch>();
            var entries = new ConcurrentBag<TextureEntry>();
            var others = new ConcurrentDictionary<string, int>();
            var payloads = new Dictionary<(string, long), byte[]>();
            // The report's walker does the locating; the payload is captured
            // as it goes past so it is read once.
            if (input.bundle)
                TextureReport.InspectBundle(input.path, relative, true, classDataPath, entries, others, payloads);
            else
                TextureReport.InspectSerialized(input.path, relative, true, classDataPath, entries, others, payloads);

            foreach (var entry in entries.OrderBy(e => e.AssetFile, StringComparer.Ordinal).ThenBy(e => e.PathId))
            {
                if (entry.ConversionBlocker != null)
                {
                    skipped.AddOrUpdate(entry.ConversionBlocker, 1, (_, n) => n + 1);
                    continue;
                }
                if (!payloads.TryGetValue((entry.AssetFile, entry.PathId), out var payload))
                {
                    skipped.AddOrUpdate("payload-not-captured", 1, (_, n) => n + 1);
                    continue;
                }
                byte[] encoded;
                try
                {
                    encoded = Transcode(entry, payload);
                }
                catch (Exception e)
                {
                    skipped.AddOrUpdate("encode-failed", 1, (_, n) => n + 1);
                    Console.Error.WriteLine($"    ✗ {relative}:{entry.PathId} {entry.Name}: {e.Message}");
                    continue;
                }
                if (encoded.Length != payload.Length)
                {
                    // The whole premise. Never packaged, always reported.
                    skipped.AddOrUpdate("size-changed", 1, (_, n) => n + 1);
                    Console.Error.WriteLine($"    ✗ {relative}:{entry.PathId} {entry.Name}: encoded {encoded.Length} bytes for a {payload.Length}-byte payload");
                    continue;
                }
                string digest = Sha256(encoded);
                string blobPath = Path.Combine(blobRoot, digest + ".bin");
                lock (BlobWriteLocks.GetOrAdd(digest, _ => new object()))
                {
                    if (!File.Exists(blobPath))
                    {
                        string part = blobPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.part";
                        File.WriteAllBytes(part, encoded);
                        File.Move(part, blobPath, overwrite: false);
                    }
                }
                patches.Add(new TexturePatch
                {
                    AssetFile = entry.AssetFile,
                    PathId = entry.PathId,
                    Name = entry.Name,
                    Width = entry.Width,
                    Height = entry.Height,
                    MipCount = entry.MipCount,
                    SourceFormat = entry.FormatId,
                    TargetFormat = entry.SuggestedTargetId!.Value,
                    Location = entry.DataLocation,
                    StreamPath = entry.DataLocation == "stream" ? entry.StreamPath : null,
                    StreamOffset = entry.DataLocation == "stream" ? entry.StreamOffset : 0,
                    Size = payload.Length,
                    SourceSha256 = Sha256(payload),
                    BlobSha256 = digest,
                });
                sourceFormats.AddOrUpdate(TextureFormats.Describe(entry.FormatId).Name, 1, (_, n) => n + 1);
                targetFormats.AddOrUpdate(TextureFormats.Describe(entry.SuggestedTargetId!.Value).Name, 1, (_, n) => n + 1);
                Interlocked.Add(ref sourceBytes, payload.Length);
                Interlocked.Add(ref residentBytes, entry.AndroidResidentBytes);
                Interlocked.Increment(ref textures);
            }
            if (patches.Count > 0) records[relative] = patches;
            int n = Interlocked.Increment(ref processed);
            if (n == 1 || n % 25 == 0 || n == inputs.Length)
            {
                Console.WriteLine($"  {n} / {inputs.Length} files; {Volatile.Read(ref textures)} textures  ({stopwatch.Elapsed.TotalSeconds:N0}s)");
                Console.Out.Flush();
            }
        });

        var manifest = new TexturePatchManifest
        {
            FileCount = records.Count,
            TextureCount = textures,
            SourceBytes = sourceBytes,
            AndroidResidentBytes = residentBytes,
            Etc2Bytes = sourceBytes,
        };
        foreach (var pair in sourceFormats.OrderBy(p => p.Key, StringComparer.Ordinal)) manifest.SourceFormats[pair.Key] = pair.Value;
        foreach (var pair in targetFormats.OrderBy(p => p.Key, StringComparer.Ordinal)) manifest.TargetFormats[pair.Key] = pair.Value;
        foreach (var pair in skipped.OrderBy(p => p.Key, StringComparer.Ordinal)) manifest.Skipped[pair.Key] = pair.Value;
        foreach (var pair in records.OrderBy(p => p.Key, StringComparer.Ordinal)) manifest.Files[pair.Key] = pair.Value;

        WritePack(outputPath, manifest, blobRoot);
        Directory.Delete(blobRoot, recursive: true);
        Audit(outputPath);
        PrintSummary(manifest, new FileInfo(outputPath).Length);
        return 0;
    }

    /// <summary>Decode every DXT level, encode it as the entry's ETC2 target, and concatenate the chain.</summary>
    internal static byte[] Transcode(TextureEntry entry, byte[] payload)
    {
        var source = TextureFormats.Describe(entry.FormatId);
        int target = entry.SuggestedTargetId ?? throw new InvalidDataException("no ETC2 target");
        int mips = Math.Max(1, entry.MipCount);
        var output = new byte[payload.Length];
        int at = 0;
        for (int level = 0; level < mips; level++)
        {
            int w = TextureFormats.MipDimension(entry.Width, level), h = TextureFormats.MipDimension(entry.Height, level);
            int size = checked((int)TextureFormats.LevelSize(source, w, h));
            if (at + size > payload.Length) throw new InvalidDataException($"level {level} runs past the payload");
            var rgba = source.Family == TextureFamily.Bc7
                ? Bc7.DecodeLevel(payload.AsSpan(at, size), w, h)
                : Dxt.DecodeLevel(source, payload.AsSpan(at, size), w, h);
            var encoded = Etc2.EncodeLevel(target, rgba, w, h);
            if (encoded.Length != size) throw new InvalidDataException($"level {level}: {encoded.Length} bytes for {size}");
            encoded.CopyTo(output, at);
            at += size;
        }
        if (at != payload.Length) throw new InvalidDataException($"chain is {at} bytes, payload {payload.Length}");
        return output;
    }

    internal static void WritePack(string outputPath, TexturePatchManifest manifest, string blobRoot)
    {
        string part = outputPath + ".part";
        File.Delete(part);
        var written = new HashSet<string>(StringComparer.Ordinal);
        using (var file = File.Create(part))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
            using (var stream = manifestEntry.Open())
                JsonSerializer.Serialize(stream, manifest, JsonOptions);
            foreach (var pair in manifest.Files.OrderBy(x => x.Key, StringComparer.Ordinal))
            foreach (var item in pair.Value)
            {
                if (!written.Add(item.BlobSha256)) continue;
                // Block-compressed texture data does not deflate usefully;
                // stored keeps the pack readable at line speed on the device.
                var entry = zip.CreateEntry($"blobs/{item.BlobSha256}.bin", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                using var source = File.OpenRead(Path.Combine(blobRoot, item.BlobSha256 + ".bin"));
                source.CopyTo(stream);
            }
        }
        File.Move(part, outputPath, overwrite: true);
    }

    static void PrintSummary(TexturePatchManifest m, long packBytes)
    {
        Console.WriteLine();
        Console.WriteLine("  ── Texture patch pack ───────────────────────────────────────────────");
        Console.WriteLine($"  {m.TextureCount} texture(s) in {m.FileCount} file(s); encoder {m.Encoder}");
        Console.WriteLine($"  source {m.SourceBytes / 1048576.0:N1} MiB -> ETC2 {m.Etc2Bytes / 1048576.0:N1} MiB " +
                          $"(same bytes); resident on Android before {m.AndroidResidentBytes / 1048576.0:N1} MiB, " +
                          $"after {m.Etc2Bytes / 1048576.0:N1} MiB, theoretical saving {(m.AndroidResidentBytes - m.Etc2Bytes) / 1048576.0:N1} MiB");
        Console.WriteLine("  source formats: " + string.Join(", ", m.SourceFormats.Select(p => $"{p.Key}={p.Value}")));
        Console.WriteLine("  target formats: " + string.Join(", ", m.TargetFormats.Select(p => $"{p.Key}={p.Value}")));
        Console.WriteLine("  left unchanged: " + (m.Skipped.Count == 0 ? "none" : string.Join(", ", m.Skipped.Select(p => $"{p.Key}={p.Value}"))));
        Console.WriteLine($"  pack: {packBytes / 1048576.0:N1} MiB");
        Console.WriteLine("  Capacity figures for the whole tree; scene residency is what the device's TextureProbe log shows.");
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────");
    }

    // ── the pack, opened ─────────────────────────────────────────────────────

    internal sealed class Pack : IDisposable
    {
        readonly FileStream _file;
        readonly ZipArchive _zip;
        readonly object _readLock = new();
        public TexturePatchManifest Manifest { get; }
        public string ManifestSha256 { get; }

        public Pack(string path)
        {
            _file = File.OpenRead(path);
            _zip = new ZipArchive(_file, ZipArchiveMode.Read);
            var entry = _zip.GetEntry("manifest.json") ?? throw new InvalidDataException("texture patch pack has no manifest.json");
            byte[] bytes;
            using (var stream = entry.Open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            ManifestSha256 = Sha256(bytes);
            Manifest = JsonSerializer.Deserialize<TexturePatchManifest>(bytes, JsonOptions)
                ?? throw new InvalidDataException("texture patch manifest is empty");
            if (Manifest.Format != PatchFormat || Manifest.Contract != Contract)
                throw new InvalidDataException(
                    $"texture patch pack uses contract {Manifest.Contract}; this build understands {Contract}");
            if (Manifest.FileCount != Manifest.Files.Count || Manifest.TextureCount != Manifest.Files.Values.Sum(v => v.Count))
                throw new InvalidDataException("texture patch manifest totals do not match its records");
        }

        public byte[] Blob(string digest)
        {
            lock (_readLock)
            {
                var entry = _zip.GetEntry($"blobs/{digest}.bin") ?? throw new InvalidDataException($"texture blob {digest} is missing");
                using var stream = entry.Open();
                using var output = new MemoryStream(checked((int)entry.Length));
                stream.CopyTo(output);
                byte[] bytes = output.ToArray();
                if (Sha256(bytes) != digest) throw new InvalidDataException($"texture blob {digest} failed SHA-256 verification");
                return bytes;
            }
        }

        public bool HasBlob(string digest) => _zip.GetEntry($"blobs/{digest}.bin") != null;

        public void Dispose()
        {
            _zip.Dispose();
            _file.Dispose();
        }
    }

    public static int Audit(string path)
    {
        using var pack = new Pack(path);
        int blobs = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, patches) in pack.Manifest.Files)
        foreach (var p in patches)
        {
            var target = TextureFormats.Describe(p.TargetFormat);
            var source = TextureFormats.Describe(p.SourceFormat);
            if (TextureFormats.SameSizeEtc2Target(source, p.TargetFormat == TextureFormats.ETC2_RGBA1)?.Id != p.TargetFormat)
                throw new InvalidDataException($"{file}:{p.PathId}: {source.Name} -> {target.Name} is not a same-size mapping");
            long expected = TextureFormats.ChainSize(target, p.Width, p.Height, p.MipCount);
            if (expected != p.Size || TextureFormats.ChainSize(source, p.Width, p.Height, p.MipCount) != p.Size)
                throw new InvalidDataException($"{file}:{p.PathId}: size {p.Size} does not fit {p.Width}x{p.Height} mips={p.MipCount}");
            if (p.Location != "inline" && p.Location != "stream")
                throw new InvalidDataException($"{file}:{p.PathId}: unknown location {p.Location}");
            if (!seen.Add(p.BlobSha256)) continue;
            byte[] blob = pack.Blob(p.BlobSha256);
            if (blob.Length != p.Size) throw new InvalidDataException($"{file}:{p.PathId}: blob is {blob.Length} bytes, expected {p.Size}");
            blobs++;
        }
        Console.WriteLine($"  texture patch pack: {pack.Manifest.TextureCount} texture(s), {blobs} unique blob(s), manifest sha256 {pack.ManifestSha256}");
        return 0;
    }

    // ── applying ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the pack's patches for one bundle to the loaded bundle: inline
    /// payloads through the asset's field, streamed payloads by rewriting the
    /// .resS entry. Returns how many textures were changed; a texture already
    /// carrying the target format and bytes counts as done and is skipped, so
    /// a resumed run is a no-op. Throws when a texture the pack names is
    /// missing or does not match what the pack was built from.
    /// </summary>
    internal static int ApplyToBundle(Pack pack, string relative, BundleFileInstance bundle, AssetsManager manager)
    {
        if (!pack.Manifest.Files.TryGetValue(relative, out var patches) || patches.Count == 0) return 0;
        var directory = bundle.file.BlockAndDirInfo.DirectoryInfos;
        var byAssetFile = patches.GroupBy(p => p.AssetFile, StringComparer.Ordinal);
        int changed = 0;
        // .resS bytes, read once per resource file and rewritten once.
        var resources = new Dictionary<string, (AssetBundleDirectoryInfo info, byte[] bytes, bool dirty)>(StringComparer.Ordinal);

        foreach (var group in byAssetFile)
        {
            var dirInfo = directory.FirstOrDefault(d => d.Name == group.Key && (d.Flags & 4) != 0)
                ?? throw new InvalidDataException($"{relative}: asset file {group.Key} named by the texture pack is not in this bundle");
            var afile = manager.LoadAssetsFileFromBundle(bundle, dirInfo.Name);
            bool fileDirty = false;
            foreach (var patch in group)
            {
                var asset = afile.file.GetAssetInfo(patch.PathId)
                    ?? throw new InvalidDataException($"{relative}:{patch.PathId}: texture named by the pack is missing");
                var bf = manager.GetBaseField(afile, asset) ?? throw new InvalidDataException($"{relative}:{patch.PathId}: no type tree");
                var outcome = ApplyOne(pack, patch, bf, $"{relative}:{patch.PathId}", streamPath =>
                {
                    string name = streamPath.Substring(streamPath.LastIndexOf('/') + 1);
                    if (!resources.TryGetValue(name, out var res))
                    {
                        var info = directory.FirstOrDefault(d => d.Name == name)
                            ?? throw new InvalidDataException($"{relative}: resource {name} is not in this bundle");
                        var reader = bundle.file.DataReader;
                        reader.Position = info.Offset;
                        byte[] bytes = reader.ReadBytes(checked((int)info.DecompressedSize));
                        res = (info, bytes, false);
                        resources[name] = res;
                    }
                    return res.bytes;
                }, streamPath =>
                {
                    string name = streamPath.Substring(streamPath.LastIndexOf('/') + 1);
                    var r = resources[name];
                    resources[name] = (r.info, r.bytes, true);
                });
                if (outcome == Outcome.Changed)
                {
                    asset.SetNewData(bf);
                    fileDirty = true;
                    changed++;
                }
            }
            if (fileDirty) dirInfo.SetNewData(afile.file);
        }
        foreach (var (name, res) in resources)
            if (res.dirty) res.info.SetNewData(res.bytes);
        return changed;
    }

    /// <summary>Applies the pack's patches to serialized files under <paramref name="root"/>, in place.</summary>
    public static int ApplyToSerialized(string root, string packPath, string classDataPath)
    {
        root = Path.GetFullPath(root);
        using var pack = new Pack(packPath);
        int files = 0, changed = 0;
        foreach (var (relative, patches) in pack.Manifest.Files.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || relative.EndsWith(".bundle", StringComparison.Ordinal)) continue;
            files++;
            changed += ApplySerializedFile(pack, relative, path, patches, classDataPath);
        }
        Console.WriteLine($"  texture patches: {changed} texture(s) changed in {files} serialized file(s) under {root}");
        return 0;
    }

    static int ApplySerializedFile(Pack pack, string relative, string path, List<TexturePatch> patches, string classDataPath)
    {
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDataPath);
        string directory = Path.GetDirectoryName(path)!;
        var resources = new Dictionary<string, (string path, byte[] bytes, bool dirty)>(StringComparer.Ordinal);
        int changed = 0;
        try
        {
            var afile = manager.LoadAssetsFile(path, false);
            manager.LoadClassDatabaseFromPackage(afile.file.Metadata.UnityVersion);
            foreach (var patch in patches)
            {
                var asset = afile.file.GetAssetInfo(patch.PathId)
                    ?? throw new InvalidDataException($"{relative}:{patch.PathId}: texture named by the pack is missing");
                var bf = manager.GetBaseField(afile, asset) ?? throw new InvalidDataException($"{relative}:{patch.PathId}: no type tree");
                var outcome = ApplyOne(pack, patch, bf, $"{relative}:{patch.PathId}", streamPath =>
                {
                    if (!resources.TryGetValue(streamPath, out var res))
                    {
                        string resPath = Path.Combine(directory, streamPath.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(resPath)) throw new InvalidDataException($"{relative}: resource {streamPath} is missing");
                        res = (resPath, File.ReadAllBytes(resPath), false);
                        resources[streamPath] = res;
                    }
                    return res.bytes;
                }, name => { var r = resources[name]; resources[name] = (r.path, r.bytes, true); });
                if (outcome == Outcome.Changed)
                {
                    asset.SetNewData(bf);
                    changed++;
                }
            }
            if (changed > 0)
            {
                string temp = path + ".tmp";
                using (var file = File.Create(temp))
                using (var writer = new AssetsFileWriter(file))
                    afile.file.Write(writer);
                manager.UnloadAll();
                File.Move(temp, path, overwrite: true);
            }
        }
        finally { manager.UnloadAll(); }
        foreach (var (_, res) in resources)
        {
            if (!res.dirty) continue;
            string temp = res.path + ".tmp";
            File.WriteAllBytes(temp, res.bytes);
            File.Move(temp, res.path, overwrite: true);
        }
        if (changed > 0) VerifySerializedFile(pack, relative, path, patches, classDataPath);
        return changed;
    }

    internal enum Outcome { Changed, AlreadyApplied }

    /// <summary>
    /// One texture: checks the asset is the one the pack was built from
    /// (dimensions, mips, format and payload digest), then writes the ETC2
    /// bytes into the same place and flips the format. Idempotent.
    /// </summary>
    internal static Outcome ApplyOne(
        Pack pack, TexturePatch patch, AssetTypeValueField bf, string what,
        Func<string, byte[]> resource, Action<string> markDirty)
    {
        int format = bf["m_TextureFormat"].AsInt;
        int width = bf["m_Width"].AsInt, height = bf["m_Height"].AsInt;
        int mips = bf["m_MipCount"].IsDummy ? 1 : bf["m_MipCount"].AsInt;
        if (width != patch.Width || height != patch.Height || mips != patch.MipCount)
            throw new InvalidDataException($"{what}: {width}x{height} mips={mips} is not the {patch.Width}x{patch.Height} mips={patch.MipCount} texture the pack was built from");

        var stream = bf["m_StreamData"];
        bool streamed = !stream.IsDummy && stream["size"].AsUInt > 0 && !string.IsNullOrEmpty(stream["path"].AsString);
        if (streamed != (patch.Location == "stream"))
            throw new InvalidDataException($"{what}: payload location changed since the pack was built");

        if (streamed)
        {
            string path = stream["path"].AsString;
            long offset = (long)stream["offset"].AsULong;
            long size = stream["size"].AsUInt;
            if (size != patch.Size || offset != patch.StreamOffset)
                throw new InvalidDataException($"{what}: stream range {offset}+{size} is not the pack's {patch.StreamOffset}+{patch.Size}");
            byte[] bytes = resource(path);
            if (offset < 0 || offset + size > bytes.LongLength)
                throw new InvalidDataException($"{what}: stream range exceeds {path}");
            var span = bytes.AsSpan(checked((int)offset), checked((int)size));
            string current = Sha256(span);
            if (format == patch.TargetFormat && current == patch.BlobSha256) return Outcome.AlreadyApplied;
            if (format != patch.SourceFormat || current != patch.SourceSha256)
                throw new InvalidDataException($"{what}: format {format} / payload {current[..12]} is not what the pack was built from");
            byte[] blob = pack.Blob(patch.BlobSha256);
            if (blob.Length != size) throw new InvalidDataException($"{what}: blob is {blob.Length} bytes, range is {size}");
            blob.CopyTo(span);
            markDirty(path);
        }
        else
        {
            var image = bf["image data"];
            byte[] bytes = image.AsByteArray;
            if (bytes.LongLength != patch.Size)
                throw new InvalidDataException($"{what}: inline payload is {bytes.Length} bytes, pack expects {patch.Size}");
            string current = Sha256(bytes);
            if (format == patch.TargetFormat && current == patch.BlobSha256) return Outcome.AlreadyApplied;
            if (format != patch.SourceFormat || current != patch.SourceSha256)
                throw new InvalidDataException($"{what}: format {format} / payload {current[..12]} is not what the pack was built from");
            byte[] blob = pack.Blob(patch.BlobSha256);
            if (blob.Length != bytes.Length) throw new InvalidDataException($"{what}: blob is {blob.Length} bytes, payload is {bytes.Length}");
            image.AsByteArray = blob;
        }
        bf["m_TextureFormat"].AsInt = patch.TargetFormat;
        return Outcome.Changed;
    }

    /// <summary>
    /// Reads the written file back and proves every patched texture carries
    /// the target format and the blob's bytes. Called after every write on
    /// both the PC and the device; a failure here is a failed file.
    /// </summary>
    internal static void VerifyBundle(Pack pack, string relative, string path, string classDataPath)
    {
        if (!pack.Manifest.Files.TryGetValue(relative, out var patches) || patches.Count == 0) return;
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDataPath);
        try
        {
            var bundle = manager.LoadBundleFile(path, true);
            manager.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
            var directory = bundle.file.BlockAndDirInfo.DirectoryInfos;
            foreach (var group in patches.GroupBy(p => p.AssetFile, StringComparer.Ordinal))
            {
                var afile = manager.LoadAssetsFileFromBundle(bundle, group.Key);
                foreach (var patch in group)
                {
                    var asset = afile.file.GetAssetInfo(patch.PathId) ?? throw new InvalidDataException($"{relative}:{patch.PathId} vanished");
                    var bf = manager.GetBaseField(afile, asset) ?? throw new InvalidDataException($"{relative}:{patch.PathId}: no type tree");
                    VerifyOne(patch, bf, $"{relative}:{patch.PathId}", streamPath =>
                    {
                        string name = streamPath.Substring(streamPath.LastIndexOf('/') + 1);
                        var info = directory.FirstOrDefault(d => d.Name == name) ?? throw new InvalidDataException($"{relative}: resource {name} vanished");
                        var reader = bundle.file.DataReader;
                        reader.Position = info.Offset + patch.StreamOffset;
                        return reader.ReadBytes(checked((int)patch.Size));
                    });
                }
            }
        }
        finally { manager.UnloadAll(); }
    }

    static void VerifySerializedFile(Pack pack, string relative, string path, List<TexturePatch> patches, string classDataPath)
    {
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDataPath);
        string directory = Path.GetDirectoryName(path)!;
        try
        {
            var afile = manager.LoadAssetsFile(path, false);
            manager.LoadClassDatabaseFromPackage(afile.file.Metadata.UnityVersion);
            foreach (var patch in patches)
            {
                var asset = afile.file.GetAssetInfo(patch.PathId) ?? throw new InvalidDataException($"{relative}:{patch.PathId} vanished");
                var bf = manager.GetBaseField(afile, asset) ?? throw new InvalidDataException($"{relative}:{patch.PathId}: no type tree");
                VerifyOne(patch, bf, $"{relative}:{patch.PathId}", streamPath =>
                {
                    string resPath = Path.Combine(directory, streamPath.Replace('/', Path.DirectorySeparatorChar));
                    using var stream = File.OpenRead(resPath);
                    stream.Position = patch.StreamOffset;
                    var bytes = new byte[patch.Size];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) throw new EndOfStreamException($"{streamPath} ended early");
                        read += n;
                    }
                    return bytes;
                });
            }
        }
        finally { manager.UnloadAll(); }
    }

    /// <summary>The written texture's format and bytes against the manifest. Pure given the payload reader.</summary>
    internal static void VerifyOne(TexturePatch patch, AssetTypeValueField bf, string what, Func<string, byte[]> streamRange)
    {
        int format = bf["m_TextureFormat"].AsInt;
        if (format != patch.TargetFormat)
            throw new InvalidDataException($"{what}: written format is {format}, expected {patch.TargetFormat}");
        byte[] bytes;
        if (patch.Location == "stream")
        {
            var stream = bf["m_StreamData"];
            if (stream["size"].AsUInt != patch.Size || (long)stream["offset"].AsULong != patch.StreamOffset)
                throw new InvalidDataException($"{what}: stream range moved");
            bytes = streamRange(stream["path"].AsString);
        }
        else
        {
            bytes = bf["image data"].AsByteArray;
        }
        if (bytes.LongLength != patch.Size || Sha256(bytes) != patch.BlobSha256)
            throw new InvalidDataException($"{what}: written payload does not match the pack's blob");
    }
}
