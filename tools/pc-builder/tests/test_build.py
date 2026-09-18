import hashlib
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


MODULE_PATH = Path(__file__).parents[1] / "build.py"
VERIFY_SYSTEM_IO = Path(__file__).parents[1] / "verify-system-io.py"
REPO_ROOT = Path(__file__).parents[3]
SPEC = importlib.util.spec_from_file_location("pc_builder", MODULE_PATH)
pc_builder = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(pc_builder)
VERIFY_SPEC = importlib.util.spec_from_file_location("system_io_verifier", VERIFY_SYSTEM_IO)
system_io_verifier = importlib.util.module_from_spec(VERIFY_SPEC)
assert VERIFY_SPEC.loader is not None
VERIFY_SPEC.loader.exec_module(system_io_verifier)


class PcBuilderTests(unittest.TestCase):
    def test_launcher_signature_normalises_windows_line_endings(self):
        with tempfile.TemporaryDirectory() as first, tempfile.TemporaryDirectory() as second:
            a, b = Path(first), Path(second)
            (a / "nested").mkdir()
            (b / "nested").mkdir()
            (a / "nested/source.cs").write_bytes(b"one\r\ntwo\r\n")
            (b / "nested/source.cs").write_bytes(b"one\ntwo\n")
            (a / "binary.dll").write_bytes(b"x\r\ny")
            (b / "binary.dll").write_bytes(b"x\r\ny")
            self.assertEqual(pc_builder.launcher_signature(a), pc_builder.launcher_signature(b))

    def test_launcher_signature_uses_androids_recursive_order(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "same").mkdir()
            (root / "same/first.txt").write_text("directory")
            (root / "same.txt").write_text("file")
            expected = hashlib.sha256(b"directoryfile").hexdigest()[:16]
            self.assertEqual("2|" + expected, pc_builder.launcher_signature(root))

    def test_depot_fingerprint_covers_names_and_content(self):
        with tempfile.TemporaryDirectory() as tmp:
            data = Path(tmp)
            (data / "Managed").mkdir()
            (data / "Managed/A.dll").write_bytes(b"a")
            (data / "globalgamemanagers").write_bytes(b"g")
            before = pc_builder.depot_fingerprint(data)
            (data / "Managed/A.dll").write_bytes(b"b")
            self.assertNotEqual(before, pc_builder.depot_fingerprint(data))

    def test_gles_patch_fingerprint_covers_bundle_tree(self):
        with tempfile.TemporaryDirectory() as tmp:
            aa = Path(tmp)
            (aa / "StandaloneLinux64").mkdir()
            bundle = aa / "StandaloneLinux64/scene.bundle"
            bundle.write_bytes(b"first")
            before = pc_builder.gles_patch_fingerprint(aa)
            bundle.write_bytes(b"second version")
            self.assertNotEqual(before, pc_builder.gles_patch_fingerprint(aa))

    def test_gles_converter_contract_matches_pinned_source(self):
        dockerfile = (REPO_ROOT / "tools/docker/apk.Dockerfile").read_text()
        shader_tool = (REPO_ROOT / "tools/bundle-surgery/ShaderGles.cs").read_text()
        commit = re.search(r"SPIRV_CROSS_COMMIT=([0-9a-f]{40})", dockerfile)
        contract = re.search(r'ConverterContract = "([^"]+)"', shader_tool)
        self.assertIsNotNone(commit)
        self.assertIsNotNone(contract)
        self.assertEqual(pc_builder.GLES_PATCH_CONTRACT, contract.group(1))
        self.assertIn(commit.group(1)[:7], pc_builder.GLES_PATCH_CONTRACT)

    def test_staging_excludes_non_unityaot_core_libraries(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            unity = root / "unity"
            bcl = unity / "editor/Editor/Data/MonoBleedingEdge/lib/mono/unityaot-linux"
            engine = unity / "android/Variations/il2cpp/Managed"
            managed = root / "game/Managed"
            packages = root / "packages"
            for directory in (bcl, engine, managed, packages):
                directory.mkdir(parents=True)
            (bcl / "mscorlib.dll").write_bytes(b"unityaot")
            (engine / "System.Private.CoreLib.dll").write_bytes(b"competing")
            (managed / "Assembly-CSharp.dll").write_bytes(b"game")
            staged = pc_builder.stage_assemblies(unity, root / "game", packages, root / "work")
            self.assertEqual(b"unityaot", (staged / "mscorlib.dll").read_bytes())
            self.assertFalse((staged / "System.Private.CoreLib.dll").exists())

    def test_register_patches_is_idempotent(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            image, asm = root / "image", root / "asm"
            image.mkdir(); asm.mkdir()
            for name in ("SilksongPatches.dll", "BepInEx.dll", "0Harmony.dll"):
                (asm / name).write_bytes(b"x")
            (image / "ScriptingAssemblies.json").write_text('{"names":["Game.dll"],"types":[16]}')
            (image / "RuntimeInitializeOnLoads.json").write_text('{"root":[]}')
            entries = root / "entrypoints.json"
            entries.write_text(json.dumps({"entryPoints": [{
                "className": "Startup", "methodName": "Run", "loadTypes": 1
            }]}))
            pc_builder.register_patches(image, asm, entries)
            pc_builder.register_patches(image, asm, entries)
            scripting = json.loads((image / "ScriptingAssemblies.json").read_text())
            loads = json.loads((image / "RuntimeInitializeOnLoads.json").read_text())
            self.assertEqual(len(scripting["names"]), len(set(scripting["names"])))
            self.assertEqual(len(loads["root"]), 2)

    def test_retarget_serialized_writes_android_platform(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "asset"
            path.write_bytes(bytes(48) + b"6000.0.50f1\0" + bytes(8))
            pc_builder.retarget_serialized(path)
            data = path.read_bytes()
            at = data.index(0, 48) + 1
            self.assertEqual(int.from_bytes(data[at:at + 4], "little"), 13)

    def test_automatic_jobs_respects_sensible_bounds(self):
        self.assertGreaterEqual(pc_builder.automatic_jobs(), 1)
        self.assertLessEqual(pc_builder.automatic_jobs(), 8)

    def test_pc_uses_unmodified_unity_il2cpp_host(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            deploy = root / "unity/editor/Editor/Data/il2cpp/build/deploy"
            deploy.mkdir(parents=True)
            (deploy / "il2cpp").write_bytes(b"host")
            (deploy / "il2cpp.dll").write_bytes(b"managed")
            # A stale cache from the original broken PC implementation must
            # not win over Unity's native, self-contained deployment.
            stale = root / "cache/il2cpp-deploy"
            stale.mkdir(parents=True)
            (stale / ".silksong-prepared").write_text("")
            (stale / "il2cpp.dll").write_bytes(b"old")
            self.assertEqual(deploy, pc_builder.prepare_il2cpp(root / "unity", root / "cache"))
            self.assertTrue((deploy / "il2cpp").stat().st_mode & 0o111)

    def test_pc_il2cpp_conversion_targets_android_arm64(self):
        self.assertEqual(
            (
                "--platform=Android",
                "--architecture=ARM64",
                "--configuration=Release",
            ),
            pc_builder.IL2CPP_TARGET_ARGS,
        )

    def test_il2cpp_target_contract_invalidates_conversion_cache(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "mscorlib.dll").write_bytes(b"same assemblies")
            actual = pc_builder.tree_digest(root)

            legacy = hashlib.sha256()
            legacy.update(b"mscorlib.dll\0")
            legacy.update(b"same assemblies")
            self.assertNotEqual(legacy.hexdigest(), actual)

    def test_full_system_io_guard_invalidates_previous_target_cache(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "mscorlib.dll").write_bytes(b"same assemblies")
            current = pc_builder.tree_digest(root)

            previous = hashlib.sha256()
            previous.update(b"android-full-system-io-v1\0")
            for arg in pc_builder.IL2CPP_TARGET_ARGS:
                previous.update(arg.encode("ascii"))
                previous.update(b"\0")
            previous.update(b"mscorlib.dll\0")
            previous.update(b"same assemblies")
            self.assertNotEqual(previous.hexdigest(), current)

    def test_case_sensitivity_fallback_invalidates_verified_v2_conversion(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "mscorlib.dll").write_bytes(b"same assemblies")
            current = pc_builder.tree_digest(root)

            verified_v2 = hashlib.sha256()
            verified_v2.update(b"android-full-system-io-v2\0")
            for arg in pc_builder.IL2CPP_TARGET_ARGS:
                verified_v2.update(arg.encode("ascii"))
                verified_v2.update(b"\0")
            verified_v2.update(b"mscorlib.dll\0")
            verified_v2.update(b"same assemblies")
            self.assertNotEqual(verified_v2.hexdigest(), current)
            self.assertNotEqual(pc_builder.PC_BUILD_CONTRACT, pc_builder.CONVERSION_CACHE_CONTRACT)

    def test_system_io_guard_accepts_constant_false_fallback(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "mscorlib.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() {
                    return (bool)0;
                }
                void FileStream__ctor_mBBBB() {
                    il2cpp_codegen_get_not_supported_exception("unused FileStream overload");
                }
                """,
                encoding="utf-8",
            )
            result = subprocess.run(
                [
                    sys.executable, str(VERIFY_SYSTEM_IO), str(root),
                    "--require-case-insensitive-fallback",
                ],
                text=True, capture_output=True,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("constant false fallback", result.stdout)

    def test_system_io_guard_accepts_il2cpps_nested_false_cast(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "mscorlib.cpp").write_text(
                "bool PathInternal_GetIsCaseSensitive_mAAAA() { { return (bool)0; } }",
                encoding="utf-8",
            )
            analysis = system_io_verifier.analyze_sources(
                root, require_case_insensitive_fallback=True,
            )
            self.assertEqual(
                {"PathInternal_GetIsCaseSensitive_mAAAA"}, analysis["patched_paths"],
            )

    def test_system_io_guard_rejects_nested_fallback_with_sibling_statement(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "mscorlib.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() {
                    { trace_probe(); return (bool)0; }
                }
                """,
                encoding="utf-8",
            )
            with self.assertRaises(SystemExit):
                system_io_verifier.analyze_sources(
                    root, require_case_insensitive_fallback=True,
                )

    def test_system_io_guard_requires_every_pathinternal_fallback(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "System.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() { return false; }
                bool PathInternal_GetIsCaseSensitive_mDDDD() { FileStream__ctor_mBBBB(); }
                void FileStream__ctor_mBBBB() { open_file(); }
                """,
                encoding="utf-8",
            )
            result = subprocess.run(
                [
                    sys.executable, str(VERIFY_SYSTEM_IO), str(root),
                    "--require-case-insensitive-fallback",
                ],
                text=True, capture_output=True,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("PathInternal_GetIsCaseSensitive_mDDDD", result.stderr)

    def test_binary_guard_rejects_old_pathinternal_object_after_fallback_patch(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "System.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() { return (bool)0; }
                void FileStream__ctor_mBBBB() { open_file(); }
                """,
                encoding="utf-8",
            )
            analysis = system_io_verifier.analyze_sources(
                root, require_case_insensitive_fallback=True,
            )
            defined = analysis["paths"] | analysis["ctors"]
            graph = {
                "PathInternal_GetIsCaseSensitive_mAAAA": {"FileStream__ctor_mBBBB"},
                "FileStream__ctor_mBBBB": set(),
            }
            with self.assertRaisesRegex(SystemExit, "patched .* still calls FileStream"):
                system_io_verifier.verify_binary_graph(analysis, defined, graph)

    def test_system_io_guard_follows_delegating_constructor(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "System.Private.CoreLib.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() {
                    FileStream__ctor_mBBBB();
                }
                void FileStream__ctor_mBBBB() {
                    FileStream__ctor_mCCCC();
                }
                void FileStream__ctor_mCCCC() {
                    il2cpp_codegen_get_not_supported_exception("FileStream");
                }
                """,
                encoding="utf-8",
            )
            result = subprocess.run(
                [sys.executable, str(VERIFY_SYSTEM_IO), str(root)],
                text=True, capture_output=True,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("FileStream__ctor_mBBBB -> FileStream__ctor_mCCCC", result.stderr)

    def test_system_io_guard_accepts_implemented_constructor_chain(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            # IL2CPP emits references/declarations in files that sort before
            # the implementation. The guard must continue past those rather
            # than treating the first occurrence as the definition.
            (root / "A_References.cpp").write_text(
                "void FileStream__ctor_mBBBB();\n",
                encoding="utf-8",
            )
            (root / "System.Private.CoreLib.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() {
                    FileStream__ctor_mBBBB();
                }
                void FileStream__ctor_mBBBB() {
                    FileStream__ctor_mCCCC();
                }
                void FileStream__ctor_mCCCC() {
                    open_file();
                }
                """,
                encoding="utf-8",
            )
            result = subprocess.run(
                [sys.executable, str(VERIFY_SYSTEM_IO), str(root)],
                text=True, capture_output=True,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("1 PathInternal definition(s)", result.stdout)
            self.assertIn("2 reachable constructor definition(s)", result.stdout)
            self.assertIn("FileStream__ctor_mBBBB", result.stdout)
            self.assertIn("FileStream__ctor_mCCCC", result.stdout)

    def test_system_io_guard_enumerates_every_pathinternal_definition(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "Safe.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() { FileStream__ctor_mBBBB(); }
                void FileStream__ctor_mBBBB() { open_file(); }
                """,
                encoding="utf-8",
            )
            (root / "Unsafe.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mDDDD() { FileStream__ctor_mEEEE(); }
                void FileStream__ctor_mEEEE() {
                    il2cpp_codegen_get_not_supported_exception("FileStream");
                }
                """,
                encoding="utf-8",
            )
            result = subprocess.run(
                [sys.executable, str(VERIFY_SYSTEM_IO), str(root)],
                text=True, capture_output=True,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("PathInternal_GetIsCaseSensitive_mDDDD -> FileStream__ctor_mEEEE", result.stderr)
            self.assertIn("Unsafe.cpp", result.stderr)

    def test_system_io_guard_checks_every_definition_of_one_constructor_symbol(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "A_Safe.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() { FileStream__ctor_mBBBB(); }
                void FileStream__ctor_mBBBB() { open_file(); }
                """,
                encoding="utf-8",
            )
            (root / "Z_Unsafe.cpp").write_text(
                """
                void FileStream__ctor_mBBBB() {
                    il2cpp_codegen_get_not_supported_exception("FileStream");
                }
                """,
                encoding="utf-8",
            )
            result = subprocess.run(
                [sys.executable, str(VERIFY_SYSTEM_IO), str(root)],
                text=True, capture_output=True,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("Z_Unsafe.cpp", result.stderr)

    def test_binary_guard_rejects_an_edge_not_present_in_verified_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "System.IO.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() { FileStream__ctor_mBBBB(); }
                void FileStream__ctor_mBBBB() { FileStream__ctor_mCCCC(); }
                void FileStream__ctor_mCCCC() { open_file(); }
                void FileStream__ctor_mDDDD() {
                    il2cpp_codegen_get_not_supported_exception("FileStream");
                }
                """,
                encoding="utf-8",
            )
            analysis = system_io_verifier.analyze_sources(root)
            defined = analysis["paths"] | analysis["ctors"]
            graph = {
                "PathInternal_GetIsCaseSensitive_mAAAA": {"FileStream__ctor_mDDDD"},
                "FileStream__ctor_mDDDD": {system_io_verifier.UNSUPPORTED},
            }
            with self.assertRaisesRegex(SystemExit, "absent from its verified source path"):
                system_io_verifier.verify_binary_graph(analysis, defined, graph)

    def test_binary_guard_accepts_the_verified_linked_graph(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "System.IO.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() { FileStream__ctor_mBBBB(); }
                void FileStream__ctor_mBBBB() { FileStream__ctor_mCCCC(); }
                void FileStream__ctor_mCCCC() { open_file(); }
                """,
                encoding="utf-8",
            )
            analysis = system_io_verifier.analyze_sources(root)
            defined = analysis["paths"] | analysis["ctors"]
            graph = {
                "PathInternal_GetIsCaseSensitive_mAAAA": {"FileStream__ctor_mBBBB"},
                "FileStream__ctor_mBBBB": {"FileStream__ctor_mCCCC"},
                "FileStream__ctor_mCCCC": set(),
            }
            system_io_verifier.verify_binary_graph(analysis, defined, graph)

    def test_binary_guard_rejects_a_missing_nonterminal_constructor_edge(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "System.IO.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() { FileStream__ctor_mBBBB(); }
                void FileStream__ctor_mBBBB() { FileStream__ctor_mCCCC(); }
                void FileStream__ctor_mCCCC() { open_file(); }
                """,
                encoding="utf-8",
            )
            analysis = system_io_verifier.analyze_sources(root)
            defined = analysis["paths"] | analysis["ctors"]
            graph = {
                "PathInternal_GetIsCaseSensitive_mAAAA": {"FileStream__ctor_mBBBB"},
                "FileStream__ctor_mBBBB": set(),
                "FileStream__ctor_mCCCC": set(),
            }
            with self.assertRaisesRegex(SystemExit, "no visible constructor edge"):
                system_io_verifier.verify_binary_graph(analysis, defined, graph)

    def test_binary_guard_parses_aarch64_objdump_branches(self):
        output = """
0000000000010000 <PathInternal_GetIsCaseSensitive_mAAAA>:
   10000:       bl      0x10100 <FileStream__ctor_mBBBB>
0000000000010100 <FileStream__ctor_mBBBB>:
   10100:       b       <FileStream__ctor_mCCCC>
0000000000010200 <FileStream__ctor_mCCCC>:
   10200:       bl      FileStream__ctor_mDDDD@plt
0000000000010300 <FileStream__ctor_mDDDD>:
   10300:       d65f03c0        ret
0000000000010400 <FileStream__ctor_mEEEE>:
   10400:       bl      0x10500
0000000000010500 <FileStream__ctor_mFFFF>:
   10500:       ret
0000000000010600 <FileStream__ctor_mABAB>:
   10600:       b       0x10608 <FileStream__ctor_mABAB+0x8>
   10604:       b       0x1060c <FileStream__ctor_mABAB+0xc>
   10608:       ret
"""
        self.assertEqual(
            {
                "PathInternal_GetIsCaseSensitive_mAAAA": {"FileStream__ctor_mBBBB"},
                "FileStream__ctor_mBBBB": {"FileStream__ctor_mCCCC"},
                "FileStream__ctor_mCCCC": {"FileStream__ctor_mDDDD"},
                "FileStream__ctor_mDDDD": set(),
                "FileStream__ctor_mEEEE": {"FileStream__ctor_mFFFF"},
                "FileStream__ctor_mFFFF": set(),
                "FileStream__ctor_mABAB": set(),
            },
            system_io_verifier.parse_objdump(output),
        )

    @unittest.skipUnless(
        Path(os.environ.get("ANDROID_NDK_ROOT", "/opt/android-sdk/ndk/27.2.12479018"),
             "toolchains/llvm/prebuilt/linux-x86_64/bin/clang++").is_file(),
        "pinned Android NDK unavailable",
    )
    def test_binary_guard_inspects_a_real_linked_aarch64_elf(self):
        ndk = Path(os.environ.get("ANDROID_NDK_ROOT", "/opt/android-sdk/ndk/27.2.12479018"))
        host = ndk / "toolchains/llvm/prebuilt/linux-x86_64"
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = root / "System.IO.cpp"
            source.write_text(
                """
                #define KEEP extern "C" __attribute__((visibility("default"), noinline, used))
                KEEP void FileStream__ctor_mCCCC() {}
                KEEP void FileStream__ctor_mBBBB() { FileStream__ctor_mCCCC(); }
                KEEP bool PathInternal_GetIsCaseSensitive_mAAAA() {
                    FileStream__ctor_mBBBB();
                    return true;
                }
                """,
                encoding="utf-8",
            )
            binary = root / "libil2cpp.so"
            subprocess.run([
                str(host / "bin/clang++"), "--target=aarch64-linux-android30",
                "-shared", "-fPIC", "-fuse-ld=lld", "-nostdlib", "-Wl,--no-undefined",
                "-O0", str(source), "-o", str(binary),
            ], check=True, capture_output=True, text=True)
            result = subprocess.run([
                sys.executable, str(VERIFY_SYSTEM_IO), str(root),
                "--binary", str(binary),
                "--nm", str(host / "bin/llvm-nm"),
                "--objdump", str(host / "bin/llvm-objdump"),
            ], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("verified linked ARM64 System.IO graph", result.stdout)

    @unittest.skipUnless(
        Path(os.environ.get("ANDROID_NDK_ROOT", "/opt/android-sdk/ndk/27.2.12479018"),
             "toolchains/llvm/prebuilt/linux-x86_64/bin/clang++").is_file(),
        "pinned Android NDK unavailable",
    )
    def test_binary_guard_rejects_a_real_stale_aarch64_object(self):
        ndk = Path(os.environ.get("ANDROID_NDK_ROOT", "/opt/android-sdk/ndk/27.2.12479018"))
        host = ndk / "toolchains/llvm/prebuilt/linux-x86_64"
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            # This is the tree the audit is asked to trust: PathInternal has
            # been patched to the constant fallback. The old constructors may
            # still exist, but none may be reachable from that root.
            (root / "System.IO.cpp").write_text(
                """
                bool PathInternal_GetIsCaseSensitive_mAAAA() { return (bool)0; }
                void FileStream__ctor_mBBBB() { FileStream__ctor_mCCCC(); }
                void FileStream__ctor_mCCCC() { open_file(); }
                void FileStream__ctor_mDDDD() {
                    il2cpp_codegen_get_not_supported_exception("FileStream");
                }
                """,
                encoding="utf-8",
            )
            # Deliberately link the old PathInternal and BBBB bodies, mirroring
            # the device's observed CF0 -> 158 stale-object path. The .cc
            # suffix keeps this fixture out of the generated-source scan.
            linked = root / "linked.cc"
            linked.write_text(
                """
                #define KEEP extern "C" __attribute__((visibility("default"), noinline, used))
                KEEP void il2cpp_codegen_get_not_supported_exception(const char*) {}
                KEEP void FileStream__ctor_mCCCC() {}
                KEEP void FileStream__ctor_mDDDD() {
                    il2cpp_codegen_get_not_supported_exception("FileStream");
                }
                KEEP void FileStream__ctor_mBBBB() { FileStream__ctor_mDDDD(); }
                KEEP bool PathInternal_GetIsCaseSensitive_mAAAA() {
                    FileStream__ctor_mBBBB();
                    return true;
                }
                """,
                encoding="utf-8",
            )
            binary = root / "libil2cpp.so"
            subprocess.run([
                str(host / "bin/clang++"), "--target=aarch64-linux-android30",
                "-shared", "-fPIC", "-fuse-ld=lld", "-nostdlib", "-Wl,--no-undefined",
                "-O0", str(linked), "-o", str(binary),
            ], check=True, capture_output=True, text=True)
            result = subprocess.run([
                sys.executable, str(VERIFY_SYSTEM_IO), str(root),
                "--binary", str(binary),
                "--nm", str(host / "bin/llvm-nm"),
                "--objdump", str(host / "bin/llvm-objdump"),
                "--require-case-insensitive-fallback",
            ], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("patched PathInternal_GetIsCaseSensitive_mAAAA still calls FileStream", result.stderr)
            self.assertIn("FileStream__ctor_mBBBB", result.stderr)

    def test_texture_report_argv_names_the_surgery_command(self):
        argv = pc_builder.texture_report_argv(
            Path("/w/BundleSurgery.dll"), Path("/game/Data"), Path("/out/texture-report.json"), True)
        self.assertEqual(
            ["dotnet", "/w/BundleSurgery.dll", "texture-report", "/game/Data", "/out/texture-report.json"],
            [str(a) for a in argv])
        argv = pc_builder.texture_report_argv(
            Path("/w/BundleSurgery.dll"), Path("/game/Data"), Path("/out/texture-report.json"), False)
        self.assertEqual("--skip-payload-scan", argv[-1])

    def test_texture_report_mode_needs_no_build_arguments(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            data = root / "depot/Hollow Knight Silksong_Data"
            (data / "Managed").mkdir(parents=True)
            (data / "globalgamemanagers").write_bytes(b"g")
            (data / "Managed/Assembly-CSharp.dll").write_bytes(b"a")
            (root / "depot/UnityPlayer.so").write_bytes(b"u")
            calls = []

            def fake_run(argv, *, cwd=None, env=None):
                calls.append([str(a) for a in argv])
                Path(argv[4]).write_text("{}", encoding="utf-8")

            original_run, original_argv = pc_builder.run, sys.argv
            pc_builder.run = fake_run
            sys.argv = [
                "build.py", "--repo", str(REPO_ROOT), "--depot", str(root / "depot"),
                "--output", str(root / "out"), "--texture-report", "--skip-payload-scan",
            ]
            try:
                self.assertEqual(0, pc_builder.main())
            finally:
                pc_builder.run, sys.argv = original_run, original_argv
            self.assertEqual(1, len(calls))
            self.assertEqual("texture-report", calls[0][2])
            self.assertEqual(str(data), calls[0][3])
            self.assertTrue(calls[0][4].endswith("texture-report.json"))
            self.assertEqual("--skip-payload-scan", calls[0][-1])
            self.assertTrue((root / "out/texture-report.json").is_file())

    def test_game_build_still_requires_its_arguments(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            data = root / "depot/Hollow Knight Silksong_Data"
            (data / "Managed").mkdir(parents=True)
            (data / "globalgamemanagers").write_bytes(b"g")
            (data / "Managed/Assembly-CSharp.dll").write_bytes(b"a")
            (root / "depot/UnityPlayer.so").write_bytes(b"u")
            original_argv = sys.argv
            sys.argv = ["build.py", "--repo", str(REPO_ROOT), "--depot", str(root / "depot"), "--output", str(root / "out")]
            try:
                with self.assertRaises(SystemExit) as raised:
                    pc_builder.main()
            finally:
                sys.argv = original_argv
            self.assertNotEqual(0, raised.exception.code)

    def _fake_texture_pack(self, root: Path, contract: str | None = None) -> Path:
        manifest = {
            "format": 1, "contract": contract or pc_builder.TEXTURE_PATCH_CONTRACT, "encoder": "silksong-etc2-1",
            "targetFamily": "etc2", "fileCount": 1, "textureCount": 3, "sourceBytes": 1000,
            "androidResidentBytes": 4000, "etc2Bytes": 1000,
            "sourceFormats": {"DXT5 (BC3)": 2, "DXT1 (BC1)": 1}, "targetFormats": {"ETC2_RGBA8": 2, "ETC2_RGB": 1},
            "skipped": {"crunched": 4}, "files": {"aa/x.bundle": []},
        }
        pack = root / "texture-patches.zip"
        with zipfile.ZipFile(pack, "w") as archive:
            archive.writestr("manifest.json", json.dumps(manifest))
        return pack

    def test_texture_patch_contract_matches_bundle_surgery_and_the_importer(self):
        surgery = (REPO_ROOT / "tools/bundle-surgery/TextureTranscode.cs").read_text(encoding="utf-8")
        etc2 = (REPO_ROOT / "tools/bundle-surgery/Etc2.cs").read_text(encoding="utf-8")
        importer = (REPO_ROOT / "src/SilksongLauncher.Launcher/app/src/main/kotlin/dev/silksong/launcher/PcBuildImport.kt").read_text(encoding="utf-8")
        rule = re.search(r'Contract = "([^"]+)/" \+ Etc2\.EncoderVersion', surgery).group(1)
        encoder = re.search(r'EncoderVersion = "([^"]+)"', etc2).group(1)
        self.assertEqual(f"{rule}/{encoder}", pc_builder.TEXTURE_PATCH_CONTRACT)
        self.assertIn(f'TEXTURE_PATCH_CONTRACT = "{pc_builder.TEXTURE_PATCH_CONTRACT}"', importer)
        self.assertIn(f'PC_BUILD_CONTRACT_V2 = "{pc_builder.PC_BUILD_CONTRACT}"', importer)

    def test_texture_patch_summary_reads_the_pack_manifest(self):
        with tempfile.TemporaryDirectory() as tmp:
            pack = self._fake_texture_pack(Path(tmp))
            fields = pc_builder.texture_patch_summary(pack)
            self.assertEqual("etc2", fields["textureFormat"])
            self.assertEqual("3", fields["textureConvertedCount"])
            self.assertEqual("DXT1 (BC1):1,DXT5 (BC3):2", fields["textureSourceFormats"])
            self.assertEqual("crunched:4", fields["textureSkipped"])
            self.assertEqual("4000", fields["textureRgba32FallbackBytes"])
            self.assertEqual("1000", fields["textureEtc2Bytes"])
            self.assertEqual("3000", fields["textureTheoreticalSavingBytes"])
            with zipfile.ZipFile(pack) as archive:
                expected = hashlib.sha256(archive.read("manifest.json")).hexdigest()
            self.assertEqual(expected, fields["textureManifestSha256"])
            stale = self._fake_texture_pack(Path(tmp) / "stale", contract="dxt-to-etc2-same-size-v0/x") if (Path(tmp) / "stale").mkdir() is None else None
            with self.assertRaises(SystemExit):
                pc_builder.texture_patch_summary(stale)

    def test_etc2_bundle_names_textures_and_records_the_profile(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            payloads = {}
            for name in ("libil2cpp.so", "libunity.so", "libmain.so", "data.apk", "classes.jar"):
                path = root / name
                path.write_bytes((name + "\n").encode())
                payloads[name] = path
            pack = self._fake_texture_pack(root)
            payloads["texture-patches.zip"] = pack
            fields = pc_builder.texture_patch_summary(pack)
            bundle = pc_builder.write_bundle(root / "out", "1.2.3", "2|signature", "a" * 64, payloads, "vulkan", fields)
            self.assertEqual("SilksongAndroid-1.2.3-ETC2-PC-Build.zip", bundle.name)
            gles = pc_builder.write_bundle(root / "out2", "1.2.3", "2|signature", "a" * 64, payloads, "gles3", fields)
            self.assertEqual("SilksongAndroid-1.2.3-OpenGLES3-ETC2-PC-Build.zip", gles.name)
            with zipfile.ZipFile(bundle) as archive:
                manifest = archive.read("manifest.properties").decode("ascii")
                self.assertIn(f"pcBuildContract={pc_builder.PC_BUILD_CONTRACT}\n", manifest)
                self.assertIn("textureFormat=etc2\n", manifest)
                self.assertIn(f"texturePatchContract={pc_builder.TEXTURE_PATCH_CONTRACT}\n", manifest)
                self.assertIn("textureConvertedCount=3\n", manifest)
                self.assertIn(f"textureManifestSha256={fields['textureManifestSha256']}\n", manifest)
                self.assertIn("texture_patches.zip.sha256=", manifest)
                self.assertIn("payload/texture-patches.zip", archive.namelist())
            pc_builder.verify_bundle_payloads(bundle, payloads)
            # Native builds say so and carry no pack.
            del payloads["texture-patches.zip"]
            native = pc_builder.write_bundle(root / "out3", "1.2.3", "2|signature", "a" * 64, payloads)
            self.assertEqual("SilksongAndroid-1.2.3-PC-Build.zip", native.name)
            with zipfile.ZipFile(native) as archive:
                self.assertIn("textureFormat=native\n", archive.read("manifest.properties").decode("ascii"))
            # An ETC2 build without its pack is refused.
            with self.assertRaises(SystemExit):
                pc_builder.write_bundle(root / "out4", "1.2.3", "2|signature", "a" * 64, payloads, "vulkan", fields)

    def test_texture_patch_fingerprint_follows_the_depot_and_the_contract(self):
        with tempfile.TemporaryDirectory() as tmp:
            data = Path(tmp)
            (data / "StreamingAssets/aa").mkdir(parents=True)
            (data / "StreamingAssets/aa/x.bundle").write_bytes(b"x")
            (data / "resources.assets").write_bytes(b"r")
            (data / "resources.assets.resS").write_bytes(b"s")
            before = pc_builder.texture_patch_fingerprint(data)
            self.assertEqual(before, pc_builder.texture_patch_fingerprint(data))
            (data / "resources.assets.resS").write_bytes(b"ss")
            self.assertNotEqual(before, pc_builder.texture_patch_fingerprint(data))
            self.assertIn(pc_builder.TEXTURE_PATCH_CONTRACT, pc_builder.TEXTURE_PATCH_CONTRACT)

    def test_entrypoint_and_wrapper_propagate_the_texture_format(self):
        entrypoint = (REPO_ROOT / "tools/docker/apk-entrypoint.sh").read_text(encoding="utf-8")
        self.assertIn('--texture-format "${PC_TEXTURE_FORMAT:-native}"', entrypoint)
        wrapper = (REPO_ROOT / "Build-On-Windows.ps1").read_text(encoding="utf-8")
        self.assertIn('[ValidateSet("Native", "ETC2")]', wrapper)
        self.assertIn('PC_TEXTURE_FORMAT=etc2', wrapper)
        cmd = (REPO_ROOT / "Build-On-Windows.cmd").read_text(encoding="utf-8")
        self.assertIn('-TextureFormat %TEX%', cmd)
        self.assertIn('set "TEX=ETC2"', cmd)

    # ── the native build script, driven with a stub clang ────────────────────

    STUB_CLANG = r'''#!/bin/bash
# A clang that emulates the two behaviours the build script depends on:
# it writes whatever -o names, and it refuses a precompiled header whose
# recorded sysroot header mtime is not the current one, with clang's words.
out=""; lang=""; pch=""
args=("$@")
for ((i=0; i<${#args[@]}; i++)); do
  case "${args[$i]}" in
    -o) out="${args[$((i+1))]}";;
    -x) lang="${args[$((i+1))]}";;
    -include-pch) pch="${args[$((i+1))]}";;
    --version) echo "stub clang version 18.0.0"; exit 0;;
  esac
done
sysroot_header=$(ls -d "$SILKSONG_STUB_SYSROOT"/usr/include/c++/v1/string.h)
now=$(stat -c %Y "$sysroot_header")
if [[ "$lang" == *-header ]]; then printf '%s' "$now" > "$out"; exit 0; fi
if [[ -n "$pch" ]] && [[ "$(cat "$pch")" != "$now" ]]; then
  echo "fatal error: file 'string.h' has been modified since the precompiled header '$pch' was built" >&2
  exit 1
fi
[[ -n "$out" ]] && printf 'obj' > "$out"
exit 0
'''

    def _native_fixture(self, root: Path) -> dict[str, str]:
        usr = root / "usr/bin"; usr.mkdir(parents=True)
        (root / "usr/lib").mkdir()
        for name in ("clang", "clang++"):
            (usr / name).write_text(self.STUB_CLANG, encoding="utf-8")
            (usr / name).chmod(0o755)
        sysroot = root / "sysroot"
        (sysroot / "usr/include/c++/v1").mkdir(parents=True)
        (sysroot / "usr/include/c++/v1/string.h").write_text("// string\n")
        il2cpp = root / "libil2cpp"
        (il2cpp / "pch").mkdir(parents=True)
        (il2cpp / "pch/pch-cpp.hpp").write_text("// pch\n")
        (il2cpp / "pch/pch-c.h").write_text("// pch c\n")
        (il2cpp / "il2cpp-config.h").write_text("// config\n")
        (il2cpp / "os/ClassLibraryPAL/brotli/include").mkdir(parents=True)
        (il2cpp / "os/ClassLibraryPAL/brotli/dec.c").write_text("int b;\n")
        (il2cpp / "runtime.cpp").write_text("int r;\n")
        external = root / "external"
        (external / "bdwgc/extra").mkdir(parents=True)
        (external / "bdwgc/include").mkdir()
        (external / "bdwgc/libatomic_ops/src").mkdir(parents=True)
        (external / "bdwgc/extra/gc.c").write_text("int g;\n")
        (external / "zlib").mkdir()
        (external / "zlib/adler.c").write_text("int z;\n")
        (external / "baselib/Include").mkdir(parents=True)
        (external / "baselib/Platforms/Android/Include").mkdir(parents=True)
        (root / "baselib.a").write_bytes(b"!<arch>\n")
        cpp = root / "cpp"
        cpp.mkdir()
        (cpp / "Bulk_A.cpp").write_text("int a;\n")
        (cpp / "Bulk_B.cpp").write_text("int b;\n")
        (cpp / "Il2CppGenericMethodPointerTable.c").write_text("int c;\n")
        return {
            "ROOT": str(root), "USR": str(root / "usr"), "SYSROOT": str(sysroot),
            "LIBIL2CPP": str(il2cpp), "EXTERNAL": str(external), "BASELIB": str(root / "baselib.a"),
            "CPPDIR": str(cpp), "BUILD_JOBS": "2", "SILKSONG_STUB_SYSROOT": str(sysroot),
            "PATH": os.environ.get("PATH", ""),
        }

    def _run_native(self, env: dict[str, str]) -> str:
        script = REPO_ROOT / "tools/ondevice-il2cpp/build-il2cpp.sh"
        result = subprocess.run(["bash", str(script)], cwd=env["ROOT"], env=env, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return result.stdout + result.stderr

    def test_native_build_survives_a_sysroot_whose_headers_were_reextracted(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            env = self._native_fixture(root)
            first = self._run_native(env)
            self.assertIn("2 rebuilt, 0 unchanged", first)
            pch = root / "obj/pch-cpp.pch"
            self.assertTrue(pch.is_file())
            self.assertTrue((root / "obj/pch-cpp.pch.identity").is_file())
            # The NDK was re-extracted: same bytes, new mtime, as a rebuilt
            # Docker layer produces. Without the identity stamp clang rejects
            # the PCH and every translation unit fails.
            header = root / "sysroot/usr/include/c++/v1/string.h"
            os.utime(header, (header.stat().st_atime + 100000, header.stat().st_mtime + 100000))
            (root / "cpp/Bulk_A.cpp").write_text("int a2;\n")
            second = self._run_native(env)
            self.assertIn("rebuilding pch-cpp.pch", second)
            self.assertIn("1 rebuilt, 1 unchanged", second)
            # The notice must not have leaked into the compiler flags.
            self.assertNotIn("### rebuilding", second.split("### PHASE A")[1].split("### PHASE B")[0])
            self.assertEqual(str(int(header.stat().st_mtime)), pch.read_text())
            self.assertNotIn("PCH rejected", (root / "err.log").read_text())

    def test_native_build_compiles_without_a_pch_clang_still_rejects(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            env = self._native_fixture(root)
            self._run_native(env)
            # Corrupt the PCH so clang rejects it while the identity stamp
            # still matches: the retry path, not the rebuild path.
            (root / "obj/pch-cpp.pch").write_text("stale")
            (root / "cpp/Bulk_B.cpp").write_text("int b2;\n")
            out = self._run_native(env)
            self.assertIn("1 rebuilt, 1 unchanged", out)
            self.assertIn("PCH rejected for Bulk_B.cpp; compiling without it", (root / "err.log").read_text())
            self.assertTrue((root / "libil2cpp.so").is_file())

    def test_bundle_uses_the_importers_exact_entry_names(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            payloads = {}
            for name in ("libil2cpp.so", "libunity.so", "libmain.so", "data.apk", "classes.jar"):
                path = root / name
                path.write_bytes((name + "\n").encode())
                payloads[name] = path
            bundle = pc_builder.write_bundle(root / "out", "1.2.3", "2|signature", "a" * 64, payloads)
            with zipfile.ZipFile(bundle) as archive:
                self.assertEqual(
                    {"manifest.properties"} | {f"payload/{name}" for name in payloads},
                    set(archive.namelist()),
                )
                manifest = archive.read("manifest.properties").decode("ascii")
                self.assertIn(f"pcBuildContract={pc_builder.PC_BUILD_CONTRACT}\n", manifest)
                self.assertIn("graphicsApi=vulkan\n", manifest)
                for name, path in payloads.items():
                    self.assertIn(f"{name}.size={path.stat().st_size}\n", manifest)
                    self.assertIn(f"{name}.sha256={pc_builder.sha256(path)}\n", manifest)
            pc_builder.verify_bundle_payloads(bundle, payloads)

    def test_gles_bundle_names_backend_and_includes_shader_patches(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            payloads = {}
            for name in (
                "libil2cpp.so", "libunity.so", "libmain.so", "data.apk",
                "classes.jar", "gles-shaders.zip",
            ):
                path = root / name
                path.write_bytes((name + "\n").encode())
                payloads[name] = path
            bundle = pc_builder.write_bundle(
                root / "out", "1.2.3", "2|signature", "a" * 64,
                payloads, "gles3",
            )
            self.assertIn("OpenGLES3", bundle.name)
            with zipfile.ZipFile(bundle) as archive:
                manifest = archive.read("manifest.properties").decode("ascii")
                self.assertIn("graphicsApi=gles3\n", manifest)
                self.assertIn("gles_shaders.zip.sha256=", manifest)
                self.assertIn("payload/gles-shaders.zip", archive.namelist())

    def test_bundle_verification_rejects_a_payload_different_from_the_built_library(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            payloads = {}
            for name in ("libil2cpp.so", "libunity.so", "libmain.so", "data.apk", "classes.jar"):
                path = root / name
                path.write_bytes((name + "\n").encode())
                payloads[name] = path
            bundle = pc_builder.write_bundle(root / "out", "1.2.3", "2|signature", "a" * 64, payloads)
            payloads["libil2cpp.so"].write_bytes(b"different linked library")
            with self.assertRaisesRegex(SystemExit, "signed bundle payload mismatch for libil2cpp.so"):
                pc_builder.verify_bundle_payloads(bundle, payloads)

    @unittest.skipUnless(shutil.which("keytool") and shutil.which("jarsigner"), "JDK signing tools unavailable")
    def test_bundle_can_be_signed_and_verified(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            bundle = root / "bundle.zip"
            with zipfile.ZipFile(bundle, "w") as archive:
                archive.writestr("manifest.properties", "format=1\n")
                archive.writestr("payload/example", b"payload")
            keystore = root / "test.jks"
            subprocess.run([
                "keytool", "-genkeypair", "-keystore", str(keystore),
                "-storetype", "JKS", "-storepass", "testing", "-keypass", "testing",
                "-alias", "bundle", "-keyalg", "RSA", "-keysize", "2048",
                "-validity", "1", "-dname", "CN=PC bundle test",
            ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            pc_builder.sign_bundle(bundle, keystore, "testing", "testing", "bundle")
            with zipfile.ZipFile(bundle) as archive:
                self.assertIn("META-INF/MANIFEST.MF", archive.namelist())


if __name__ == "__main__":
    unittest.main()
