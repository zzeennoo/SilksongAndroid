using AssetsTools.NET;
using AssetsTools.NET.Extra;
using LZ4ps;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BundleSurgery;

/// <summary>
/// Builds and applies an Android OpenGL ES shader patch set.
///
/// The Linux depot has GLCore and Vulkan programs, but no GLES programs. A
/// platform-id-only rewrite therefore produces an Android player with no
/// usable shaders. The expensive/fragile part of the conversion is done on
/// the PC: SPIRV-Cross translates every referenced Vulkan program to ESSL
/// 3.10, and this class stores only the converted shader blobs in a deduplicated
/// ZIP. The device-side command merely installs those already-verified blobs
/// while it performs the normal in-place bundle retarget.
/// </summary>
internal static class ShaderGles
{
    internal const int PatchFormat = 1;
    internal const int AndroidBuildTarget = 13;
    internal const int VulkanPlatform = 18;
    internal const int GlesPlatform = 9;
    internal const sbyte SpirvProgram = 25;
    internal const sbyte Gles31Program = 3;
    internal const int CurrentBlobVersion = 202012090;
    // The converter revision is part of both the cache path and the signed
    // patch manifest. Changing the pinned SPIRV-Cross source can change ESSL
    // output even when the input SPIR-V is identical, so it must be an
    // explicit cache/compatibility boundary.
    internal const string ConverterContract = "spirv-cross-be71ee8-essl310-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
    private static readonly ConcurrentDictionary<string, object> BlobWriteLocks = new(StringComparer.Ordinal);

    internal sealed class PatchManifest
    {
        public int Format { get; set; } = PatchFormat;
        public string GraphicsApi { get; set; } = "gles3";
        public string ConverterContract { get; set; } = ShaderGles.ConverterContract;
        public int BundleCount { get; set; }
        public int ShaderCount { get; set; }
        public int ProgramCount { get; set; }
        public Dictionary<string, List<ShaderPatch>> Bundles { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class ShaderPatch
    {
        public string AssetFile { get; set; } = "";
        public long PathId { get; set; }
        public string Name { get; set; } = "";
        public string BlobSha256 { get; set; } = "";
        public uint[] Offsets { get; set; } = Array.Empty<uint>();
        public uint[] CompressedLengths { get; set; } = Array.Empty<uint>();
        public uint[] DecompressedLengths { get; set; } = Array.Empty<uint>();
        public int ProgramCount { get; set; }
    }

    internal sealed class ConvertedShader
    {
        public byte[] CompressedBlob { get; init; } = Array.Empty<byte>();
        public uint[] Offsets { get; init; } = Array.Empty<uint>();
        public uint[] CompressedLengths { get; init; } = Array.Empty<uint>();
        public uint[] DecompressedLengths { get; init; } = Array.Empty<uint>();
        public int ProgramCount { get; init; }
    }

    private sealed class BlobEntry
    {
        public int Offset;
        public int Length;
        public int Segment;
        public byte[] Bytes = Array.Empty<byte>();
    }

    /// <summary>A single decompressed Unity shader segment.</summary>
    private sealed class BlobSegment
    {
        public BlobEntry[] Entries { get; }
        public int SegmentIndex { get; }

        internal BlobSegment(BlobEntry[] entries, int segmentIndex)
        {
            Entries = entries;
            SegmentIndex = segmentIndex;
        }

        public static BlobSegment Read(byte[] bytes, int segmentIndex)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream);
            if (bytes.Length < 4) throw new InvalidDataException("shader segment has no entry table");
            int count = reader.ReadInt32();
            if (count < 0 || count > 2_000_000 || 4L + count * 12L > bytes.Length)
                throw new InvalidDataException($"shader segment has invalid entry count {count}");

            var entries = new BlobEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = new BlobEntry
                {
                    Offset = reader.ReadInt32(),
                    Length = reader.ReadInt32(),
                    Segment = reader.ReadInt32(),
                };
            }
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Segment != segmentIndex) continue;
                if (entry.Offset < 0 || entry.Length < 0 ||
                    (long)entry.Offset + entry.Length > bytes.Length)
                    throw new InvalidDataException($"shader entry {i} points outside segment {segmentIndex}");
                entry.Bytes = bytes.AsSpan(entry.Offset, entry.Length).ToArray();
            }
            return new BlobSegment(entries, segmentIndex);
        }

        public byte[] Write()
        {
            int headerSize = checked(4 + Entries.Length * 12);
            int cursor = headerSize;
            foreach (var entry in Entries)
            {
                if (entry.Segment != SegmentIndex) continue;
                cursor = Align4(cursor);
                entry.Offset = cursor;
                entry.Length = entry.Bytes.Length;
                cursor = checked(cursor + entry.Length);
            }

            using var stream = new MemoryStream(Align4(cursor));
            using var writer = new BinaryWriter(stream);
            writer.Write(Entries.Length);
            foreach (var entry in Entries)
            {
                writer.Write(entry.Offset);
                writer.Write(entry.Length);
                writer.Write(entry.Segment);
            }
            foreach (var entry in Entries)
            {
                if (entry.Segment != SegmentIndex) continue;
                while (stream.Position < entry.Offset) writer.Write((byte)0);
                writer.Write(entry.Bytes);
            }
            while ((stream.Position & 3) != 0) writer.Write((byte)0);
            return stream.ToArray();
        }
    }

    private sealed class SubProgram
    {
        public int BlobVersion;
        public int ProgramType;
        public int StatsAlu;
        public int StatsTex;
        public int StatsFlow;
        public int StatsTemp;
        public List<string> Keywords = new();
        public byte[] ProgramData = Array.Empty<byte>();
        public byte[] Trailer = Array.Empty<byte>();

        public static SubProgram Read(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (bytes.Length < 32) throw new InvalidDataException("shader subprogram is truncated");
            var result = new SubProgram
            {
                BlobVersion = reader.ReadInt32(),
                ProgramType = reader.ReadInt32(),
                StatsAlu = reader.ReadInt32(),
                StatsTex = reader.ReadInt32(),
                StatsFlow = reader.ReadInt32(),
                StatsTemp = reader.ReadInt32(),
            };
            if (result.BlobVersion != CurrentBlobVersion)
                throw new InvalidDataException(
                    $"shader blob version {result.BlobVersion} is not the verified Unity 6 layout {CurrentBlobVersion}");

            int keywordCount = reader.ReadInt32();
            if (keywordCount < 0 || keywordCount > 100_000)
                throw new InvalidDataException($"invalid shader keyword count {keywordCount}");
            for (int i = 0; i < keywordCount; i++)
            {
                int length = reader.ReadInt32();
                if (length < 0 || length > bytes.Length - stream.Position)
                    throw new InvalidDataException("shader keyword points outside its entry");
                result.Keywords.Add(Encoding.UTF8.GetString(reader.ReadBytes(length)));
                stream.Position = Align4(checked((int)stream.Position));
            }

            int programLength = reader.ReadInt32();
            if (programLength < 0 || programLength > bytes.Length - stream.Position)
                throw new InvalidDataException("shader program points outside its entry");
            result.ProgramData = reader.ReadBytes(programLength);
            stream.Position = Align4(checked((int)stream.Position));
            result.Trailer = reader.ReadBytes(checked((int)(stream.Length - stream.Position)));
            return result;
        }

        public byte[] Write()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(BlobVersion);
            writer.Write(ProgramType);
            writer.Write(StatsAlu);
            writer.Write(StatsTex);
            writer.Write(StatsFlow);
            writer.Write(StatsTemp);
            writer.Write(Keywords.Count);
            foreach (string keyword in Keywords)
            {
                byte[] value = Encoding.UTF8.GetBytes(keyword);
                writer.Write(value.Length);
                writer.Write(value);
                while ((stream.Position & 3) != 0) writer.Write((byte)0);
            }
            writer.Write(ProgramData.Length);
            writer.Write(ProgramData);
            while ((stream.Position & 3) != 0) writer.Write((byte)0);
            writer.Write(Trailer);
            return stream.ToArray();
        }
    }

    internal static ConvertedShader Convert(AssetTypeValueField shader, string description)
    {
        int platformIndex = FindPlatform(shader, VulkanPlatform);
        if (platformIndex < 0)
            throw new InvalidDataException($"{description}: no Vulkan shader slice is available to translate");

        var offsets = ReadPlatformValues(shader["offsets.Array"], platformIndex);
        var compressedLengths = ReadPlatformValues(shader["compressedLengths.Array"], platformIndex);
        var decompressedLengths = ReadPlatformValues(shader["decompressedLengths.Array"], platformIndex);
        if (offsets.Length == 0 || offsets.Length != compressedLengths.Length ||
            offsets.Length != decompressedLengths.Length)
            throw new InvalidDataException($"{description}: inconsistent Vulkan shader segment tables");

        byte[] sourceBlob = shader["compressedBlob.Array"].AsByteArray;
        var segments = new BlobSegment[offsets.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            if ((long)offsets[i] + compressedLengths[i] > sourceBlob.Length)
                throw new InvalidDataException($"{description}: Vulkan segment {i} points outside compressedBlob");
            byte[] compressed = sourceBlob.AsSpan((int)offsets[i], (int)compressedLengths[i]).ToArray();
            byte[] decompressed = DecodeLz4(compressed, checked((int)decompressedLengths[i]));
            segments[i] = BlobSegment.Read(decompressed, i);
        }

        HashSet<int> programIndices = CollectProgramIndices(shader, mutate: true);
        int converted = 0;
        foreach (int index in programIndices.Order())
        {
            int hits = 0;
            foreach (var segment in segments)
            {
                if (index < 0 || index >= segment.Entries.Length)
                    throw new InvalidDataException($"{description}: program blob index {index} is outside the segment table");
                var raw = segment.Entries[index];
                if (raw.Segment != segment.SegmentIndex) continue;
                hits++;
                var program = SubProgram.Read(raw.Bytes);
                if (program.ProgramType != SpirvProgram)
                    throw new InvalidDataException(
                        $"{description}: blob {index} metadata says SPIR-V but entry type is {program.ProgramType}");
                program.ProgramData = SpirvCross.Convert(program.ProgramData, $"{description} blob {index}");
                program.ProgramType = Gles31Program;
                raw.Bytes = program.Write();
                converted++;
            }
            if (hits != 1)
                throw new InvalidDataException($"{description}: program blob {index} appears in {hits} shader segments");
        }

        var outputOffsets = new uint[segments.Length];
        var outputCompressed = new uint[segments.Length];
        var outputDecompressed = new uint[segments.Length];
        using var blob = new MemoryStream();
        for (int i = 0; i < segments.Length; i++)
        {
            byte[] decompressed = segments[i].Write();
            byte[] compressed = LZ4Codec.Encode32HC(decompressed, 0, decompressed.Length);
            outputOffsets[i] = checked((uint)blob.Position);
            outputCompressed[i] = checked((uint)compressed.Length);
            outputDecompressed[i] = checked((uint)decompressed.Length);
            blob.Write(compressed);
        }

        var result = new ConvertedShader
        {
            CompressedBlob = blob.ToArray(),
            Offsets = outputOffsets,
            CompressedLengths = outputCompressed,
            DecompressedLengths = outputDecompressed,
            ProgramCount = converted,
        };
        Apply(shader, result);
        AuditConverted(shader, description, converted);
        return result;
    }

    internal static void Apply(AssetTypeValueField shader, ConvertedShader patch)
    {
        int sourceIndex = FindPlatform(shader, GlesPlatform);
        if (sourceIndex < 0) sourceIndex = FindPlatform(shader, VulkanPlatform);
        if (sourceIndex < 0)
            throw new InvalidDataException("shader has neither a Vulkan nor an existing GLES platform slot");

        SetSinglePlatform(shader, sourceIndex, GlesPlatform, patch.Offsets,
            patch.CompressedLengths, patch.DecompressedLengths, patch.CompressedBlob);
        CollectProgramIndices(shader, mutate: true);
    }

    private static void AuditConverted(AssetTypeValueField shader, string description, int expectedPrograms)
    {
        if (FindPlatform(shader, GlesPlatform) != 0 || shader["platforms.Array"].AsArray.size != 1)
            throw new InvalidDataException($"{description}: GLES platform rewrite did not stick");
        if (CollectProgramIndices(shader, mutate: false).Count != 0)
            throw new InvalidDataException($"{description}: SPIR-V metadata remains after conversion");
        if (expectedPrograms <= 0)
            throw new InvalidDataException($"{description}: no referenced SPIR-V programs were converted");
    }

    private static HashSet<int> CollectProgramIndices(AssetTypeValueField shader, bool mutate)
    {
        var result = new HashSet<int>();
        void Visit(AssetTypeValueField field)
        {
            AssetTypeValueField? gpu = null;
            AssetTypeValueField? blob = null;
            foreach (var child in field.Children)
            {
                if (child.FieldName == "m_GpuProgramType") gpu = child;
                else if (child.FieldName == "m_BlobIndex") blob = child;
            }
            if (gpu is not null && blob is not null && gpu.AsSByte == SpirvProgram)
            {
                result.Add(checked((int)blob.AsUInt));
                if (mutate) gpu.AsSByte = Gles31Program;
            }
            foreach (var child in field.Children) Visit(child);
        }
        Visit(shader["m_ParsedForm"]);
        return result;
    }

    private static int FindPlatform(AssetTypeValueField shader, int platform)
    {
        var platforms = shader["platforms.Array"];
        for (int i = 0; i < platforms.AsArray.size; i++)
            if (platforms[i].AsInt == platform) return i;
        return -1;
    }

    private static uint[] ReadPlatformValues(AssetTypeValueField outer, int platformIndex)
    {
        var value = outer[platformIndex];
        if (value.Children.Count > 0 && value.Children[0].FieldName == "Array")
            return value["Array"].Children.Select(x => x.AsUInt).ToArray();
        return new[] { value.AsUInt };
    }

    private static void WritePlatformValues(AssetTypeValueField outer, int platformIndex, uint[] values)
    {
        var value = outer[platformIndex];
        if (value.Children.Count > 0 && value.Children[0].FieldName == "Array")
        {
            var array = value["Array"];
            if (array.Children.Count != values.Length)
                throw new InvalidDataException(
                    $"shader segment count changed from {array.Children.Count} to {values.Length}");
            for (int i = 0; i < values.Length; i++) array[i].AsUInt = values[i];
            var info = array.AsArray; info.size = values.Length; array.AsArray = info;
        }
        else
        {
            if (values.Length != 1) throw new InvalidDataException("legacy shader table cannot hold multiple segments");
            value.AsUInt = values[0];
        }
    }

    private static void SetSinglePlatform(
        AssetTypeValueField shader,
        int sourceIndex,
        int platform,
        uint[] offsets,
        uint[] compressedLengths,
        uint[] decompressedLengths,
        byte[] compressedBlob)
    {
        var platforms = shader["platforms.Array"];
        var offsetFields = shader["offsets.Array"];
        var compressedFields = shader["compressedLengths.Array"];
        var decompressedFields = shader["decompressedLengths.Array"];

        if (sourceIndex != 0)
        {
            platforms.Children[0] = platforms.Children[sourceIndex];
            offsetFields.Children[0] = offsetFields.Children[sourceIndex];
            compressedFields.Children[0] = compressedFields.Children[sourceIndex];
            decompressedFields.Children[0] = decompressedFields.Children[sourceIndex];
        }
        platforms[0].AsInt = platform;
        WritePlatformValues(offsetFields, 0, offsets);
        WritePlatformValues(compressedFields, 0, compressedLengths);
        WritePlatformValues(decompressedFields, 0, decompressedLengths);

        static void Truncate(AssetTypeValueField field)
        {
            field.Children = new List<AssetTypeValueField> { field.Children[0] };
            var info = field.AsArray; info.size = 1; field.AsArray = info;
        }
        Truncate(platforms);
        Truncate(offsetFields);
        Truncate(compressedFields);
        Truncate(decompressedFields);
        shader["compressedBlob.Array"].AsByteArray = compressedBlob;
    }

    private static byte[] DecodeLz4(byte[] compressed, int expectedLength)
    {
        var output = new byte[expectedLength];
        int decoded = LZ4Codec.Decode32(compressed, 0, compressed.Length, output, 0, output.Length, true);
        if (decoded != expectedLength)
            throw new InvalidDataException($"LZ4 shader segment decoded {decoded} bytes, expected {expectedLength}");
        return output;
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static string Sha256(byte[] bytes) => System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static class SpirvCross
    {
        private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);

        internal static void RunForTest(string executable, IEnumerable<string> arguments) =>
            Run(executable, arguments, "GLES self-test");

        public static byte[] Convert(byte[] spirv, string description)
        {
            if (spirv.Length < 20 || BitConverter.ToUInt32(spirv, 0) != 0x07230203)
                throw new InvalidDataException($"{description}: program is not a SPIR-V module");
            string stage = ReadStage(spirv, description);
            string digest = Sha256(spirv);
            string cache = Environment.GetEnvironmentVariable("SILKSONG_GLES_SHADER_CACHE")
                ?? Path.Combine(Path.GetTempPath(), "silksong-gles-shaders");
            cache = Path.Combine(cache, ConverterContract);
            Directory.CreateDirectory(cache);
            string final = Path.Combine(cache, digest + ".essl");
            string receipt = final + ".sha256";

            lock (Locks.GetOrAdd(digest, _ => new object()))
            {
                if (File.Exists(final) && File.Exists(receipt))
                {
                    byte[] cached = File.ReadAllBytes(final);
                    string recorded = File.ReadAllText(receipt, Encoding.ASCII).Trim();
                    if (recorded == Sha256(cached)) return Validate(cached, stage, description);
                }
                File.Delete(final);
                File.Delete(receipt);

                string token = $"{Environment.ProcessId}-{Environment.CurrentManagedThreadId}-{Guid.NewGuid():N}";
                string input = Path.Combine(cache, digest + "." + token + ".spv");
                string output = Path.Combine(cache, digest + "." + token + ".essl");
                try
                {
                    File.WriteAllBytes(input, spirv);
                    var crossArgs = new List<string>
                    {
                        input, "--es", "--version", "310", "--output", output,
                    };
                    // Vulkan's clip-space depth is [0,w]; OpenGL expects
                    // [-w,w]. Unity normally bakes this distinction while
                    // compiling each platform slice, so cross-converting the
                    // Vulkan slice has to perform the same vertex fixup.
                    if (stage == "vert") crossArgs.Add("--fixup-clipspace");
                    Run("spirv-cross", crossArgs, description);
                    byte[] result = Validate(File.ReadAllBytes(output), stage, description);

                    string validator = Environment.GetEnvironmentVariable("GLSLANG_VALIDATOR") ?? "glslangValidator";
                    Run(validator, new[] { "-S", stage, output }, description);
                    File.Move(output, final, overwrite: true);
                    File.WriteAllText(receipt, Sha256(result) + "\n", Encoding.ASCII);
                    return result;
                }
                finally
                {
                    File.Delete(input);
                    File.Delete(output);
                }
            }
        }

        private static byte[] Validate(byte[] source, string stage, string description)
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(source); }
            catch (DecoderFallbackException e) { throw new InvalidDataException($"{description}: SPIRV-Cross emitted invalid UTF-8", e); }
            if (!text.Contains("#version 310 es", StringComparison.Ordinal))
                throw new InvalidDataException($"{description}: translated {stage} shader is not ESSL 3.10");
            if (text.Contains("layout(set", StringComparison.Ordinal) ||
                text.Contains("layout (set", StringComparison.Ordinal))
                throw new InvalidDataException($"{description}: Vulkan descriptor-set syntax remains in ESSL");
            return source;
        }

        private static string ReadStage(byte[] spirv, string description)
        {
            int words = spirv.Length / 4;
            for (int at = 5; at < words;)
            {
                uint op = BitConverter.ToUInt32(spirv, at * 4);
                int wordCount = (int)(op >> 16);
                int opcode = (int)(op & 0xffff);
                if (wordCount <= 0 || at + wordCount > words)
                    throw new InvalidDataException($"{description}: malformed SPIR-V instruction stream");
                if (opcode == 15 && wordCount >= 3) // OpEntryPoint
                {
                    uint model = BitConverter.ToUInt32(spirv, (at + 1) * 4);
                    return model switch
                    {
                        0 => "vert",
                        4 => "frag",
                        5 => "comp",
                        _ => throw new InvalidDataException(
                            $"{description}: SPIR-V execution model {model} requires GLES 3.2 and is not in this profile"),
                    };
                }
                at += wordCount;
            }
            throw new InvalidDataException($"{description}: SPIR-V module has no entry point");
        }

        private static void Run(string executable, IEnumerable<string> arguments, string description)
        {
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"could not start {executable}");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new InvalidDataException(
                    $"{description}: {executable} failed ({process.ExitCode}): " +
                    string.Join(" ", (stderr + "\n" + stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(6)));
        }
    }

    internal static int SelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "silksong-gles-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string? fixture = Environment.GetEnvironmentVariable("SILKSONG_GLES_SELFTEST_SPIRV");
            byte[] spirv;
            if (!string.IsNullOrWhiteSpace(fixture))
            {
                spirv = File.ReadAllBytes(fixture);
            }
            else
            {
                string source = Path.Combine(root, "minimal.vert");
                string spirvPath = Path.Combine(root, "minimal.spv");
                File.WriteAllText(source,
                    "#version 450\nlayout(location=0) in vec4 position;\n" +
                    "void main(){ gl_Position = position; }\n", new UTF8Encoding(false));
                SpirvCross.RunForTest(
                    "glslangValidator", new[] { "-V", "-S", "vert", "-o", spirvPath, source });
                spirv = File.ReadAllBytes(spirvPath);
            }
            byte[] essl = SpirvCross.Convert(spirv, "GLES self-test");
            if (!Encoding.UTF8.GetString(essl).Contains("#version 310 es", StringComparison.Ordinal))
                throw new InvalidDataException("SPIRV-Cross self-test did not emit ESSL 3.10");

            var original = new SubProgram
            {
                BlobVersion = CurrentBlobVersion,
                ProgramType = SpirvProgram,
                StatsAlu = 1,
                StatsTex = 2,
                StatsFlow = 3,
                StatsTemp = 4,
                Keywords = new List<string> { "FOO", "BAR" },
                ProgramData = spirv,
                Trailer = new byte[] { 9, 8, 7, 6, 5 },
            };
            byte[] encoded = original.Write();
            var decoded = SubProgram.Read(encoded);
            if (decoded.ProgramType != SpirvProgram ||
                !decoded.ProgramData.SequenceEqual(spirv) ||
                !decoded.Trailer.SequenceEqual(original.Trailer) ||
                !decoded.Keywords.SequenceEqual(original.Keywords))
                throw new InvalidDataException("Unity shader subprogram round trip changed data");

            var segment = new BlobSegment(new[]
            {
                new BlobEntry { Segment = 0, Bytes = encoded },
                new BlobEntry { Segment = 1, Offset = 123, Length = 456 },
            }, 0);
            byte[] segmentBytes = segment.Write();
            var segmentAgain = BlobSegment.Read(segmentBytes, 0);
            if (!segmentAgain.Entries[0].Bytes.SequenceEqual(encoded) ||
                segmentAgain.Entries[1].Segment != 1)
                throw new InvalidDataException("Unity multi-segment blob round trip changed data");

            Console.WriteLine("GLES self-test passed: SPIR-V→ESSL 3.10, subprogram trailer, multi-segment table");
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static int ExtractAndroid(string inputPath, string outputPath, string classDataPath)
    {
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDataPath);
        bool isBundle;
        using (var probe = File.OpenRead(inputPath))
        {
            byte[] magic = new byte[7];
            isBundle = probe.Read(magic, 0, magic.Length) == magic.Length &&
                Encoding.ASCII.GetString(magic) == "UnityFS";
        }

        if (!isBundle)
        {
            var afile = manager.LoadAssetsFile(inputPath);
            manager.LoadClassDatabaseFromPackage(afile.file.Metadata.UnityVersion);
            int shaders = 0, programs = 0;
            foreach (var asset in afile.file.GetAssetsOfType(AssetClassID.Shader))
            {
                var field = manager.GetBaseField(afile, asset)
                    ?? throw new InvalidDataException($"shader {asset.PathId} has no type tree");
                string name = field["m_ParsedForm"]?["m_Name"]?.AsString ?? $"pathId {asset.PathId}";
                var converted = Convert(field, $"{Path.GetFileName(inputPath)}:{name}");
                asset.SetNewData(field);
                shaders++;
                programs += converted.ProgramCount;
            }
            afile.file.Metadata.TargetPlatform = AndroidBuildTarget;
            using (var file = File.Create(outputPath))
            using (var writer = new AssetsFileWriter(file))
                afile.file.Write(writer);
            manager.UnloadAll();
            Console.Error.WriteLine(
                $"  {Path.GetFileName(inputPath)}: {shaders} GLES shader(s), {programs} program(s), BuildTarget→Android");
            return 0;
        }

        var bundle = manager.LoadBundleFile(inputPath, true);
        manager.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
        int totalShaders = 0, totalPrograms = 0;
        foreach (var dirInfo in bundle.file.BlockAndDirInfo.DirectoryInfos)
        {
            if ((dirInfo.Flags & 4) == 0) continue;
            var afile = manager.LoadAssetsFileFromBundle(bundle, dirInfo.Name);
            bool changed = afile.file.Metadata.TargetPlatform != AndroidBuildTarget;
            foreach (var asset in afile.file.GetAssetsOfType(AssetClassID.Shader))
            {
                var field = manager.GetBaseField(afile, asset)
                    ?? throw new InvalidDataException($"shader {asset.PathId} has no type tree");
                string name = field["m_ParsedForm"]?["m_Name"]?.AsString ?? $"pathId {asset.PathId}";
                var converted = Convert(field, $"{Path.GetFileName(inputPath)}:{name}");
                asset.SetNewData(field);
                totalShaders++;
                totalPrograms += converted.ProgramCount;
                changed = true;
            }
            afile.file.Metadata.TargetPlatform = AndroidBuildTarget;
            if (changed) dirInfo.SetNewData(afile.file);
        }
        string temp = outputPath + ".tmp";
        using (var file = File.Create(temp))
        using (var writer = new AssetsFileWriter(file))
            bundle.file.Write(writer);
        manager.UnloadAll();
        File.Move(temp, outputPath, overwrite: true);
        Console.Error.WriteLine(
            $"  {Path.GetFileName(inputPath)}: {totalShaders} GLES shader(s), {totalPrograms} program(s), BuildTarget→Android");
        return 0;
    }

    internal static int BuildPatchArchive(string aaRoot, string outputPath, string classDataPath)
    {
        aaRoot = Path.GetFullPath(aaRoot);
        var inputs = Directory.GetFiles(aaRoot, "*.bundle", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();
        if (inputs.Length == 0) throw new InvalidDataException($"no bundles under {aaRoot}");

        var manifest = new PatchManifest { BundleCount = inputs.Length };
        var records = new ConcurrentDictionary<string, List<ShaderPatch>>(StringComparer.Ordinal);
        string blobRoot = outputPath + ".blobs";
        Directory.CreateDirectory(blobRoot);
        int done = 0, shaders = 0, programs = 0;
        int jobs = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
        Parallel.ForEach(inputs, new ParallelOptions { MaxDegreeOfParallelism = jobs }, input =>
        {
            string relative = Path.GetRelativePath(aaRoot, input).Replace('\\', '/');
            var patches = BuildBundlePatches(input, classDataPath, blobRoot);
            records[relative] = patches;
            Interlocked.Add(ref shaders, patches.Count);
            Interlocked.Add(ref programs, patches.Sum(x => x.ProgramCount));
            int n = Interlocked.Increment(ref done);
            if (n == 1 || n % 25 == 0 || n == inputs.Length)
            {
                Console.WriteLine($"  {n} / {inputs.Length} bundles; {Volatile.Read(ref shaders)} shaders; {Volatile.Read(ref programs)} programs");
                Console.Out.Flush();
            }
        });

        manifest.ShaderCount = shaders;
        manifest.ProgramCount = programs;
        foreach (var pair in records.OrderBy(x => x.Key, StringComparer.Ordinal))
            manifest.Bundles[pair.Key] = pair.Value;

        string part = outputPath + ".part";
        File.Delete(part);
        var writtenBlobs = new HashSet<string>(StringComparer.Ordinal);
        using (var file = File.Create(part))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
            using (var stream = manifestEntry.Open())
                JsonSerializer.Serialize(stream, manifest, JsonOptions);
            foreach (var pair in records.OrderBy(x => x.Key, StringComparer.Ordinal))
            foreach (var item in pair.Value)
            {
                if (!writtenBlobs.Add(item.BlobSha256)) continue;
                var entry = zip.CreateEntry($"blobs/{item.BlobSha256}.lz4", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                using var source = File.OpenRead(Path.Combine(blobRoot, item.BlobSha256 + ".lz4"));
                source.CopyTo(stream);
            }
        }
        File.Move(part, outputPath, overwrite: true);
        AuditPatchArchive(outputPath);
        Console.WriteLine(
            $"  GLES patch archive: {inputs.Length} bundles, {shaders} shaders, {programs} programs, " +
            $"{writtenBlobs.Count} unique blobs, {new FileInfo(outputPath).Length / (1024 * 1024)} MB");
        return 0;
    }

    private static List<ShaderPatch> BuildBundlePatches(string path, string classDataPath, string blobRoot)
    {
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDataPath);
        var bundle = manager.LoadBundleFile(path, true);
        manager.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
        var result = new List<ShaderPatch>();
        try
        {
            foreach (var dirInfo in bundle.file.BlockAndDirInfo.DirectoryInfos)
            {
                if ((dirInfo.Flags & 4) == 0) continue;
                var afile = manager.LoadAssetsFileFromBundle(bundle, dirInfo.Name);
                foreach (var asset in afile.file.GetAssetsOfType(AssetClassID.Shader))
                {
                    var field = manager.GetBaseField(afile, asset)
                        ?? throw new InvalidDataException($"{path}: shader {asset.PathId} has no type tree");
                    string name = field["m_ParsedForm"]?["m_Name"]?.AsString ?? $"pathId {asset.PathId}";
                    var converted = Convert(field, $"{Path.GetFileName(path)}:{name}");
                    string digest = Sha256(converted.CompressedBlob);
                    string blobPath = Path.Combine(blobRoot, digest + ".lz4");
                    lock (BlobWriteLocks.GetOrAdd(digest, _ => new object()))
                    {
                        if (!File.Exists(blobPath))
                        {
                            string part = blobPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.part";
                            File.WriteAllBytes(part, converted.CompressedBlob);
                            File.Move(part, blobPath, overwrite: false);
                        }
                    }
                    result.Add(new ShaderPatch
                    {
                        AssetFile = dirInfo.Name,
                        PathId = asset.PathId,
                        Name = name,
                        BlobSha256 = digest,
                        Offsets = converted.Offsets,
                        CompressedLengths = converted.CompressedLengths,
                        DecompressedLengths = converted.DecompressedLengths,
                        ProgramCount = converted.ProgramCount,
                    });
                }
            }
            return result;
        }
        finally { manager.UnloadAll(); }
    }

    private sealed class PatchArchive : IDisposable
    {
        private readonly FileStream _file;
        private readonly ZipArchive _zip;
        private readonly object _readLock = new();
        public PatchManifest Manifest { get; }

        public PatchArchive(string path)
        {
            _file = File.OpenRead(path);
            _zip = new ZipArchive(_file, ZipArchiveMode.Read);
            var entry = _zip.GetEntry("manifest.json")
                ?? throw new InvalidDataException("GLES patch archive has no manifest.json");
            using var stream = entry.Open();
            Manifest = JsonSerializer.Deserialize<PatchManifest>(stream, JsonOptions)
                ?? throw new InvalidDataException("GLES patch manifest is empty");
            if (Manifest.Format != PatchFormat || Manifest.GraphicsApi != "gles3" ||
                Manifest.ConverterContract != ConverterContract)
                throw new InvalidDataException("GLES patch archive uses an unsupported format or converter");
            if (Manifest.BundleCount != Manifest.Bundles.Count)
                throw new InvalidDataException("GLES patch manifest bundle count does not match its index");
        }

        public byte[] Blob(string digest)
        {
            lock (_readLock)
            {
                var entry = _zip.GetEntry($"blobs/{digest}.lz4")
                    ?? throw new InvalidDataException($"GLES patch blob {digest} is missing");
                using var stream = entry.Open();
                using var output = new MemoryStream(checked((int)entry.Length));
                stream.CopyTo(output);
                byte[] bytes = output.ToArray();
                if (Sha256(bytes) != digest)
                    throw new InvalidDataException($"GLES patch blob {digest} failed SHA-256 verification");
                return bytes;
            }
        }

        public void Dispose()
        {
            _zip.Dispose();
            _file.Dispose();
        }
    }

    internal static int AuditPatchArchive(string path)
    {
        using var archive = new PatchArchive(path);
        int shaders = 0, programs = 0;
        foreach (var bundle in archive.Manifest.Bundles.Values)
        foreach (var patch in bundle)
        {
            if (patch.Offsets.Length == 0 || patch.Offsets.Length != patch.CompressedLengths.Length ||
                patch.Offsets.Length != patch.DecompressedLengths.Length || patch.ProgramCount <= 0)
                throw new InvalidDataException($"invalid GLES patch record for {patch.Name}");
            byte[] blob = archive.Blob(patch.BlobSha256);
            for (int i = 0; i < patch.Offsets.Length; i++)
                if ((long)patch.Offsets[i] + patch.CompressedLengths[i] > blob.Length)
                    throw new InvalidDataException($"GLES patch segment points outside {patch.BlobSha256}");
            shaders++;
            programs += patch.ProgramCount;
        }
        if (shaders != archive.Manifest.ShaderCount || programs != archive.Manifest.ProgramCount)
            throw new InvalidDataException("GLES patch manifest totals do not match its records");
        return 0;
    }

    internal static int RetargetTree(
        string aaRoot,
        string groupRoot,
        string progressPath,
        string patchPath,
        string classDataPath)
    {
        aaRoot = Path.GetFullPath(aaRoot);
        groupRoot = Path.GetFullPath(groupRoot);
        var inputs = Directory.GetFiles(groupRoot, "*.bundle", SearchOption.AllDirectories);
        if (inputs.Length == 0) throw new InvalidDataException($"no bundles under {groupRoot}");

        using var archive = new PatchArchive(patchPath);
        int processed = 0, changed = 0, skipped = 0, failed = 0;
        var errors = new ConcurrentBag<string>();
        var progressLock = new object();
        var stopwatch = Stopwatch.StartNew();

        void Report(bool complete = false)
        {
            lock (progressLock)
            {
                string temp = progressPath + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(progressPath))!);
                File.WriteAllText(temp,
                    $"total={inputs.Length}\nprocessed={Volatile.Read(ref processed)}\n" +
                    $"changed={Volatile.Read(ref changed)}\nskipped={Volatile.Read(ref skipped)}\n" +
                    $"failed={Volatile.Read(ref failed)}\ncomplete={(complete ? 1 : 0)}\n");
                File.Move(temp, progressPath, overwrite: true);
                Console.WriteLine($"  {Volatile.Read(ref processed)} / {inputs.Length}  ({stopwatch.Elapsed.TotalSeconds:N0}s)");
                Console.Out.Flush();
            }
        }

        Report();
        Parallel.ForEach(inputs,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(2, Environment.ProcessorCount)) },
            input =>
            {
                string relative = Path.GetRelativePath(aaRoot, input).Replace('\\', '/');
                try
                {
                    if (!archive.Manifest.Bundles.TryGetValue(relative, out var patches))
                        throw new InvalidDataException($"GLES patch archive has no record for {relative}");
                    ApplyBundle(input, patches, archive, classDataPath);
                    Interlocked.Increment(ref changed);
                }
                catch (Exception e)
                {
                    errors.Add($"{relative}: {e.Message}");
                    Interlocked.Increment(ref failed);
                }
                int n = Interlocked.Increment(ref processed);
                if (n == 1 || n % 25 == 0 || n == inputs.Length) Report();
            });

        stopwatch.Stop();
        Report(complete: true);
        Console.WriteLine($"  {changed} GLES-retargeted, {skipped} already ready, {failed} failed in {stopwatch.Elapsed.TotalSeconds:N0}s");
        foreach (string error in errors.Take(10)) Console.Error.WriteLine($"    ✗ {error}");
        return failed == 0 ? 0 : 1;
    }

    private static void ApplyBundle(string path, List<ShaderPatch> patches, PatchArchive archive, string classDataPath)
    {
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDataPath);
        var bundle = manager.LoadBundleFile(path, true);
        manager.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
        var expected = patches.ToDictionary(x => (x.AssetFile, x.PathId));
        int found = 0;
        try
        {
            foreach (var dirInfo in bundle.file.BlockAndDirInfo.DirectoryInfos)
            {
                if ((dirInfo.Flags & 4) == 0) continue;
                var afile = manager.LoadAssetsFileFromBundle(bundle, dirInfo.Name);
                bool modified = afile.file.Metadata.TargetPlatform != AndroidBuildTarget;
                foreach (var asset in afile.file.GetAssetsOfType(AssetClassID.Shader))
                {
                    if (!expected.TryGetValue((dirInfo.Name, asset.PathId), out var patch))
                        throw new InvalidDataException(
                            $"unexpected shader {dirInfo.Name}:{asset.PathId}; patch set and depot do not match");
                    var field = manager.GetBaseField(afile, asset)
                        ?? throw new InvalidDataException($"shader {asset.PathId} has no type tree");
                    byte[] blob = archive.Blob(patch.BlobSha256);
                    Apply(field, new ConvertedShader
                    {
                        CompressedBlob = blob,
                        Offsets = patch.Offsets,
                        CompressedLengths = patch.CompressedLengths,
                        DecompressedLengths = patch.DecompressedLengths,
                        ProgramCount = patch.ProgramCount,
                    });
                    asset.SetNewData(field);
                    found++;
                    modified = true;
                }
                afile.file.Metadata.TargetPlatform = AndroidBuildTarget;
                if (modified) dirInfo.SetNewData(afile.file);
            }
            if (found != patches.Count)
                throw new InvalidDataException($"found {found} of {patches.Count} expected shaders");

            string temp = path + ".tmp";
            using (var file = File.Create(temp))
            using (var writer = new AssetsFileWriter(file))
                bundle.file.Write(writer);
            manager.UnloadAll();
            File.Delete(path);
            File.Move(temp, path);
        }
        finally { manager.UnloadAll(); }
    }
}
