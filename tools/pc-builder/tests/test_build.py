import hashlib
import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


MODULE_PATH = Path(__file__).parents[1] / "build.py"
VERIFY_SYSTEM_IO = Path(__file__).parents[1] / "verify-system-io.py"
SPEC = importlib.util.spec_from_file_location("pc_builder", MODULE_PATH)
pc_builder = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(pc_builder)


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
            previous.update(b"silksong-pc-il2cpp-v2\0")
            for arg in pc_builder.IL2CPP_TARGET_ARGS:
                previous.update(arg.encode("ascii"))
                previous.update(b"\0")
            previous.update(b"mscorlib.dll\0")
            previous.update(b"same assemblies")
            self.assertNotEqual(previous.hexdigest(), current)

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
            self.assertIn("FileStream__ctor_mBBBB -> FileStream__ctor_mCCCC", result.stdout)

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
                for name, path in payloads.items():
                    self.assertIn(f"{name}.size={path.stat().st_size}\n", manifest)
                    self.assertIn(f"{name}.sha256={pc_builder.sha256(path)}\n", manifest)

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
