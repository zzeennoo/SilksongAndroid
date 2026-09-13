import hashlib
import importlib.util
import json
import shutil
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path


MODULE_PATH = Path(__file__).parents[1] / "build.py"
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
