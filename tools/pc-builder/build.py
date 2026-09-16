#!/usr/bin/env python3
"""Build a private Android runtime bundle from a user's Linux depot.

This runs inside tools/docker/apk.Dockerfile.  It deliberately emits a bundle
for the launcher to import rather than an APK containing the game: the depot's
8 GB Addressables tree stays on the user's device, while the expensive IL2CPP
conversion and native compile happen on the PC.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path


UNITY_VERSION = "6000.0.50f1"
PACKAGE = "com.jakobkhansen.silksong"
# Bumped when the manifest gains a field the importer must understand. v2
# adds textureFormat and the optional texture patch pack; an importer that
# knows v2 still accepts v1 bundles (which are all native-texture builds).
PC_BUILD_CONTRACT = "android-texture-format-v2"
# The staged unityaot assemblies are now patched before conversion, so their
# digest already invalidates an older C++ tree. Keep a named contract as well:
# it makes the reason explicit and prevents a coincidental input hash match
# from ever crossing this compatibility boundary.
CONVERSION_CACHE_CONTRACT = "android-system-io-fallback-v1"
CONTENT_ROOT = f"/data/user/0/{PACKAGE}/files/aa"
GRAPHICS_APIS = {"vulkan": "21", "gles3": "11"}
GLES_PATCH_CONTRACT = "spirv-cross-be71ee8-essl310-v1"
TEXTURE_FORMATS = ("native", "etc2")
# Must match TextureTranscode.Contract in bundle-surgery: the same-size
# DXT -> ETC2 rule and the encoder revision that produced the bytes.
TEXTURE_PATCH_CONTRACT = "dxt-to-etc2-same-size-v1/silksong-etc2-1"
ROSLYN_VERSION = "4.12.0"
ROSLYN_BYTES = 21_775_071
ROSLYN_FILES = (
    "csc.dll",
    "csc.deps.json",
    "csc.runtimeconfig.json",
    "Microsoft.CodeAnalysis.dll",
    "Microsoft.CodeAnalysis.CSharp.dll",
)
TEXT_EXTENSIONS = {"cs", "json", "sh", "txt", "rsp", "xml", "md"}
CORE_LIBRARY_FILES = {"mscorlib.dll", "system.private.corelib.dll"}


def note(message: str) -> None:
    print(f"[pc-build] {message}", flush=True)


def fail(message: str) -> "NoReturn":
    raise SystemExit(f"[pc-build] ERROR: {message}")


def run(argv: list[str], *, cwd: Path | None = None, env: dict[str, str] | None = None) -> None:
    shown = " ".join(argv[:3]) + (" …" if len(argv) > 3 else "")
    note(shown)
    subprocess.run(argv, cwd=cwd, env=env, check=True)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def update_file(digest: "hashlib._Hash", path: Path) -> None:
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)


def find_depot_data(depot: Path) -> Path:
    level = sorted((p for p in depot.iterdir() if p.is_dir()), key=lambda p: p.name)
    for _ in range(4):
        for directory in level:
            if (
                directory.name.endswith("_Data")
                and (directory / "globalgamemanagers").is_file()
                and (directory / "Managed" / "Assembly-CSharp.dll").is_file()
            ):
                return directory
        level = sorted(
            (child for directory in level for child in directory.iterdir() if child.is_dir()),
            key=lambda p: str(p),
        )
    fail(f"no Linux Silksong *_Data directory was found below {depot}")


def depot_fingerprint(data: Path) -> str:
    """Must remain byte-for-byte equivalent to PcBuildImport.depotFingerprint."""
    files = sorted((data / "Managed").glob("*.dll"), key=lambda p: p.name)
    for name in ("globalgamemanagers", "ScriptingAssemblies.json"):
        path = data / name
        if path.is_file():
            files.append(path)
    digest = hashlib.sha256()
    for path in sorted(files, key=lambda p: p.relative_to(data).as_posix()):
        relative = path.relative_to(data).as_posix()
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        update_file(digest, path)
    return digest.hexdigest()


def launcher_signature(asset_root: Path) -> str:
    """Equivalent to BuildInstallation.signature/AssetDigest on Android."""
    if not asset_root.is_dir():
        fail(f"launcher assets were not staged at {asset_root}; build the APK first")
    digest = hashlib.sha256()

    # AssetManager walks each directory in name order before returning to its
    # siblings. A flat sort is subtly different for a directory and file that
    # share a prefix ("patches/x" versus "patches.txt"), so mirror that walk
    # exactly instead of relying on the current asset names not to expose it.
    def walk(directory: Path):
        for path in sorted(directory.iterdir(), key=lambda p: p.name):
            if path.is_dir():
                yield from walk(path)
            elif path.is_file():
                yield path

    for path in walk(asset_root):
        data = path.read_bytes()
        if path.suffix.lower().lstrip(".") in TEXT_EXTENSIONS:
            data = data.replace(b"\r\n", b"\n")
        digest.update(data)
    return "2|" + digest.hexdigest()[:16]


def automatic_jobs() -> int:
    """A conservative worker count based on the container's actual limit."""
    cpu = os.cpu_count() or 1
    memory = None
    for candidate in (Path("/sys/fs/cgroup/memory.max"), Path("/sys/fs/cgroup/memory/memory.limit_in_bytes")):
        try:
            value = candidate.read_text(encoding="ascii").strip()
            if value != "max":
                parsed = int(value)
                # Old cgroups report an enormous sentinel when unlimited.
                if 0 < parsed < (1 << 60):
                    memory = parsed
                    break
        except (OSError, ValueError):
            pass
    if memory is None:
        try:
            for line in Path("/proc/meminfo").read_text(encoding="ascii").splitlines():
                if line.startswith("MemTotal:"):
                    memory = int(line.split()[1]) * 1024
                    break
        except (OSError, ValueError, IndexError):
            pass

    # IL2CPP is much more memory hungry than clang. Budget about 2 GiB per
    # worker and cap at eight: this is intentionally a reliable default for a
    # typical 8-16 GiB Docker Desktop VM, not a benchmark setting.
    by_memory = max(1, (memory or (4 << 30)) // (2 << 30))
    return max(1, min(cpu, by_memory, 8))


def unique_dlls(*directories: Path, skip: set[str] | None = None) -> list[Path]:
    seen = set(skip or ())
    result: list[Path] = []
    for directory in directories:
        for path in sorted(directory.glob("*.dll"), key=lambda p: p.name):
            if path.name in seen:
                continue
            seen.add(path.name)
            result.append(path)
    return result


def ensure_roslyn(cache: Path) -> Path:
    out = cache / "roslyn"
    csc = out / "csc.dll"
    if all((out / name).is_file() for name in ROSLYN_FILES):
        return csc

    out.mkdir(parents=True, exist_ok=True)
    package = cache / f"microsoft.net.compilers.toolset.{ROSLYN_VERSION}.nupkg"
    if not package.is_file() or package.stat().st_size != ROSLYN_BYTES:
        package.unlink(missing_ok=True)
        url = (
            "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/"
            f"{ROSLYN_VERSION}/microsoft.net.compilers.toolset.{ROSLYN_VERSION}.nupkg"
        )
        note("fetching the pinned Roslyn compiler (22 MB, once)")
        run(["curl", "-fL", "--retry", "5", "-o", str(package) + ".part", url])
        Path(str(package) + ".part").replace(package)
    if package.stat().st_size != ROSLYN_BYTES:
        fail(f"Roslyn package has an unexpected size: {package.stat().st_size}")

    prefix = "tasks/netcore/bincore/"
    with zipfile.ZipFile(package) as archive:
        for name in ROSLYN_FILES:
            member = prefix + name
            try:
                data = archive.read(member)
            except KeyError:
                fail(f"Roslyn package does not contain {member}")
            (out / name).write_bytes(data)
    return csc


def quote_rsp(value: Path) -> str:
    return '"' + str(value).replace('"', '\\"') + '"'


def compile_cs(
    csc: Path,
    root: Path,
    name: str,
    output: Path,
    sources: list[Path],
    references: list[Path],
    defines: list[str],
    *,
    unsafe: bool = False,
    warnings: str = "0169,0414,0649,0067",
) -> None:
    if not sources:
        fail(f"no C# sources for {name}")
    output.parent.mkdir(parents=True, exist_ok=True)
    rsp = root / f"{name}.rsp"
    lines = [
        "-target:library",
        f"-out:{quote_rsp(output)}",
        "-optimize+",
        "-nostdlib+",
        "-noconfig",
        "-langversion:9.0",
        "-deterministic+",
    ]
    if warnings:
        lines.append(f"-nowarn:{warnings}")
    if unsafe:
        lines.append("-unsafe+")
    if defines:
        lines.append("-define:" + ";".join(defines))
    lines.extend(f"-reference:{quote_rsp(path)}" for path in references)
    lines.extend(quote_rsp(path) for path in sources)
    rsp.write_text("\n".join(lines) + "\n", encoding="utf-8")
    note(f"compiling {name} ({len(sources)} sources)")
    run(["dotnet", str(csc), "@" + str(rsp)], cwd=root)
    if not output.is_file() or output.stat().st_size == 0:
        fail(f"Roslyn produced no {output.name}")


def input_system_defines() -> list[str]:
    result = [
        "UNITY_ANDROID", "UNITY_ANDROID_API", "ENABLE_INPUT_SYSTEM",
        "UNITY_INPUT_SYSTEM_ENABLE_UI", "UNITY_INPUT_SYSTEM_ENABLE_PHYSICS",
        "UNITY_INPUT_SYSTEM_ENABLE_PHYSICS2D", "UNITY_INPUT_SYSTEM_ENABLE_XR",
        "UNITY_INPUT_SYSTEM_ENABLE_VR", "UNITY_INPUT_SYSTEM_ENABLE_ANALYTICS",
        "HAS_SET_LOCAL_POSITION_AND_ROTATION", "UNITY_INPUT_SYSTEM_PROJECT_WIDE_ACTIONS",
        "UNITY_INPUT_SYSTEM_INPUT_ACTIONS_EDITOR_AUTO_SAVE_ON_FOCUS_LOST",
        "UNITY_INPUT_SYSTEM_PLATFORM_SCROLL_DELTA", "UNITY_INPUT_SYSTEM_INPUT_MODULE_SCROLL_DELTA",
        "UNITY_INPUT_SYSTEM_SENDPOINTERHOVERTOPARENT", "ENABLE_VR", "ENABLE_XR",
        "ENABLE_MONO", "NET_STANDARD_2_1", "NET_STANDARD",
    ]
    for year in range(2017, 2024):
        result.extend(f"UNITY_{year}_{stream}_OR_NEWER" for stream in range(1, 4))
        result.append(f"UNITY_{year}_OR_NEWER")
    result.extend(("UNITY_6000_0_OR_NEWER", "UNITY_6000_OR_NEWER"))
    return result


def compile_packages(repo: Path, unity: Path, data: Path, root: Path, csc: Path) -> Path:
    packages = root / "packages"
    packages.mkdir(parents=True, exist_ok=True)
    bcl = unity / "editor/Editor/Data/MonoBleedingEdge/lib/mono/unityaot-linux"
    engine = unity / "android/Variations/il2cpp/Managed"
    managed = data / "Managed"
    if not (bcl / "mscorlib.dll").is_file():
        fail(f"Unity AOT class library is missing from {bcl}")
    if not engine.is_dir():
        fail(f"Unity Android managed assemblies are missing from {engine}")

    input_root = unity / "packages/com.unity.inputsystem/InputSystem"
    nested = {
        path.parent
        for path in input_root.rglob("*.asmdef")
        if path != input_root / "Unity.InputSystem.asmdef"
    }
    input_sources = [
        path for path in input_root.rglob("*.cs")
        if not any(parent == path.parent or parent in path.parents for parent in nested)
    ]
    input_refs = unique_dlls(bcl, engine, skip={"Unity.InputSystem.dll"})
    input_refs.extend(
        path for name in ("UnityEngine.UI.dll", "netstandard.dll")
        if (path := managed / name).is_file() and path.name not in {p.name for p in input_refs}
    )
    compile_cs(
        csc, root, "inputsystem", packages / "Unity.InputSystem.dll",
        sorted(input_sources), input_refs, input_system_defines(), unsafe=True,
        warnings="0169,0414,0649,3021,0067",
    )

    patch_refs = unique_dlls(bcl, engine, managed)
    io_refs = unique_dlls(bcl, engine)
    netstandard = managed / "netstandard.dll"
    if netstandard.is_file() and netstandard.name not in {p.name for p in io_refs}:
        io_refs.append(netstandard)

    compile_cs(
        csc, root, "silksong-io", packages / "SilksongIo.dll",
        sorted((repo / "tools/silksong-io/src").rglob("*.cs")), io_refs,
        ["UNITY_ANDROID"], warnings="",
    )
    compile_cs(
        csc, root, "patches", packages / "SilksongPatches.dll",
        sorted((repo / "tools/silksong-patches/src").rglob("*.cs")), patch_refs,
        ["UNITY_ANDROID", "ENABLE_INPUT_SYSTEM"],
    )
    for assembly, folder in (("0Harmony", "harmony"), ("BepInEx", "bepinex")):
        compile_cs(
            csc, root, assembly.lower(), packages / f"{assembly}.dll",
            sorted((repo / f"tools/bepinex-shim/src/{folder}").rglob("*.cs")), patch_refs,
            ["UNITY_ANDROID", "ENABLE_INPUT_SYSTEM"],
        )
    return packages


def stage_assemblies(unity: Path, data: Path, packages: Path, root: Path) -> Path:
    asm = root / "asm"
    shutil.rmtree(asm, ignore_errors=True)
    asm.mkdir(parents=True)
    sources = (
        unity / "editor/Editor/Data/MonoBleedingEdge/lib/mono/unityaot-linux",
        unity / "android/Variations/il2cpp/Managed",
        data / "Managed",
    )
    excluded_corelibs: list[Path] = []
    for index, directory in enumerate(sources):
        for dll in sorted(directory.glob("*.dll"), key=lambda p: p.name):
            # The unityaot BCL is the conversion profile's one core library.
            # Filename de-duplication already rejects the depot's mscorlib,
            # but System.Private.CoreLib has a different filename while
            # defining the same System.IO types. Passing both lets IL2CPP emit
            # two PathInternal implementations and makes the linked choice
            # dependent on assembly/object order.
            if index > 0 and dll.name.casefold() in CORE_LIBRARY_FILES:
                excluded_corelibs.append(dll)
                continue
            target = asm / dll.name
            if not target.exists():
                shutil.copy2(dll, target)
    for dll in sorted(packages.glob("*.dll"), key=lambda p: p.name):
        shutil.copy2(dll, asm / dll.name)
    if excluded_corelibs:
        note(
            "excluded competing core library file(s): "
            + ", ".join(str(path) for path in excluded_corelibs)
        )
    note(f"staged {len(list(asm.glob('*.dll')))} IL2CPP assemblies")
    return asm


def prepare_il2cpp(unity: Path, cache: Path) -> Path:
    """Return Unity's native Linux-x64 IL2CPP deployment.

    The Android builder has to remove this deployment's private x64 CoreCLR
    and host il2cpp.dll through its ARM64 runtime.  This builder itself runs in
    a Linux-x64 container, however, so doing the same surgery here discards the
    exact runtime and deps graph Unity tested together.  Use the bundled
    self-contained apphost intact on its native platform.

    ``cache`` remains in the signature because older builder caches contain a
    prepared copy there; deliberately ignoring it prevents a failed run from
    reusing that modified deployment.
    """
    source = unity / "editor/Editor/Data/il2cpp/build/deploy"
    executable = source / "il2cpp"
    if not executable.is_file() or not (source / "il2cpp.dll").is_file():
        fail(f"Unity's desktop IL2CPP deployment is incomplete: {source}")
    executable.chmod(executable.stat().st_mode | 0o111)
    return source


IL2CPP_TARGET_ARGS = (
    "--platform=Android",
    "--architecture=ARM64",
    "--configuration=Release",
)


def verify_system_io(repo: Path, cpp: Path) -> None:
    """Reject generated code when any Android file-probe path is a stub.

    The lightweight CI smoke catches converter/platform regressions in the
    Unity class library.  The game conversion is the artifact we actually
    ship, though, and its larger assembly graph has failed differently before.
    Run the same guard over that exact tree before either accepting a cache or
    spending hours compiling it.
    """
    run([
        sys.executable, str(repo / "tools/pc-builder/verify-system-io.py"), str(cpp),
        "--require-case-insensitive-fallback",
    ])


def verify_managed_system_io(repo: Path, asm: Path) -> None:
    """Report System.IO owners and reject competing File/FileStream core types."""
    surgery = repo / "tools/bundle-surgery/bin/Release/net8.0/BundleSurgery.dll"
    run(["dotnet", str(surgery), "audit-system-io", str(asm)])


def patch_managed_system_io(repo: Path, asm: Path) -> None:
    """Remove the launch-time filesystem probe from every PathInternal copy."""
    surgery = repo / "tools/bundle-surgery/bin/Release/net8.0/BundleSurgery.dll"
    run(["dotnet", str(surgery), "patch-system-io-case-sensitivity", str(asm)])


def tree_digest(directory: Path) -> str:
    digest = hashlib.sha256()
    # Conversion output is target-specific even though the input assemblies
    # are not.  Including the target contract here prevents an older PC build
    # (which accidentally let the Linux host choose the platform) from being
    # accepted after the builder is fixed.  The native object cache remains
    # content-addressed and can still reuse every generated TU that is equal.
    digest.update(CONVERSION_CACHE_CONTRACT.encode("ascii"))
    digest.update(b"\0")
    for arg in IL2CPP_TARGET_ARGS:
        digest.update(arg.encode("ascii"))
        digest.update(b"\0")
    for path in sorted(directory.glob("*.dll"), key=lambda p: p.name):
        digest.update(path.name.encode("utf-8"))
        digest.update(b"\0")
        update_file(digest, path)
    return digest.hexdigest()


def convert(repo: Path, unity: Path, root: Path, asm: Path, jobs: int) -> tuple[Path, Path]:
    surgery = repo / "tools/bundle-surgery/bin/Release/net8.0/BundleSurgery.dll"
    weaver = repo / "tools/mod-weaver/bin/Release/net8.0/ModWeaver.dll"
    run(["dotnet", str(surgery), "redirect-file-replace", str(asm / "Assembly-CSharp.dll"), str(asm / "SilksongIo.dll")])
    run(["dotnet", str(weaver), "builtin", "--assemblies", str(asm)])
    patch_managed_system_io(repo, asm)
    verify_managed_system_io(repo, asm)

    cpp = root / "cpp"
    data = root / "data"
    signature = tree_digest(asm)
    marker = root / "conversion.inputs"
    complete = (
        marker.is_file() and marker.read_text().strip() == signature
        and (data / "Metadata/global-metadata.dat").is_file()
        and len(list(cpp.glob("*.cpp"))) > 100
    )
    if complete:
        try:
            verify_system_io(repo, cpp)
        except subprocess.CalledProcessError:
            # A previous builder could mark host-targeted output complete.
            # Never reuse it just because the managed inputs are unchanged.
            note("cached IL2CPP output failed the Android System.IO guard; rebuilding it")
        else:
            note("IL2CPP conversion is current and verified; reusing it")
            return cpp, data

    shutil.rmtree(cpp, ignore_errors=True)
    shutil.rmtree(data, ignore_errors=True)
    cpp.mkdir(parents=True)
    data.mkdir(parents=True)
    deploy = prepare_il2cpp(unity, root)
    argv = [str(deploy / "il2cpp"), "--convert-to-cpp"]
    argv.extend(f"--assembly={path}" for path in sorted(asm.glob("*.dll"), key=lambda p: p.name))
    argv.extend((
        f"--generatedcppdir={cpp}", f"--data-folder={data}",
        "--dotnetprofile=unityaot-linux", "--emit-null-checks",
        "--enable-array-bounds-check", "--static-lib-il2-cpp",
        *IL2CPP_TARGET_ARGS, f"--jobs={jobs}",
    ))
    env = os.environ.copy()
    env["DOTNET_PROCESSOR_COUNT"] = str(jobs)
    note(f"converting IL to C++ with {jobs} worker(s)")
    run(argv, cwd=deploy, env=env)
    metadata = data / "Metadata/global-metadata.dat"
    source_count = len(list(cpp.glob("*.cpp"))) + len(list(cpp.glob("*.c")))
    if not metadata.is_file() or metadata.stat().st_size == 0 or source_count < 100:
        fail(f"IL2CPP output is incomplete ({source_count} sources, metadata={metadata.exists()})")
    verify_system_io(repo, cpp)
    marker.write_text(signature + "\n", encoding="utf-8")
    note(f"IL2CPP produced {source_count} native sources")
    return cpp, data


def find_android_file(android: Path, suffix: str) -> Path:
    wanted = suffix.replace("\\", "/")
    for path in android.rglob(Path(suffix).name):
        if path.is_file() and path.as_posix().endswith(wanted):
            return path
    fail(f"Unity Android module does not contain {suffix}")


def compile_native(repo: Path, unity: Path, root: Path, jobs: int) -> Path:
    ndk = Path(os.environ.get("ANDROID_NDK_ROOT", "/opt/android-sdk/ndk/27.2.12479018"))
    host = ndk / "toolchains/llvm/prebuilt/linux-x86_64"
    if not (host / "bin/clang").is_file():
        fail(f"the pinned Android NDK is missing at {ndk}")
    baselib = find_android_file(
        unity / "android",
        "Variations/il2cpp/Release/StaticLibs/arm64-v8a/baselib.a",
    )
    il2cpp = unity / "editor/Editor/Data/il2cpp"
    env = os.environ.copy()
    env.update({
        "ROOT": str(root), "USR": str(host), "SYSROOT": str(host / "sysroot"),
        "LIBIL2CPP": str(il2cpp / "libil2cpp"), "EXTERNAL": str(il2cpp / "external"),
        "BASELIB": str(baselib), "CPPDIR": str(root / "cpp"),
        "BUILD_JOBS": str(jobs), "OPT": "-O2",
    })
    note(f"cross-compiling ARM64 libil2cpp.so with {jobs} job(s)")
    run(["bash", str(repo / "tools/ondevice-il2cpp/build-il2cpp.sh")], cwd=root, env=env)
    output = root / "libil2cpp.so"
    if not output.is_file() or output.stat().st_size < 10 * 1024 * 1024:
        fail("native link produced no plausible libil2cpp.so")
    verifier = repo / "tools/pc-builder/verify-system-io.py"
    run([
        sys.executable, str(verifier), str(root / "cpp"),
        "--binary", str(output),
        "--nm", str(host / "bin/llvm-nm"),
        "--objdump", str(host / "bin/llvm-objdump"),
        "--require-case-insensitive-fallback",
    ])
    note(f"linked libil2cpp SHA-256: {sha256(output)}")
    return output


def retarget_serialized(path: Path) -> None:
    data = bytearray(path.read_bytes())
    index = 48
    while index < len(data) and data[index] != 0:
        index += 1
    if index + 4 >= len(data):
        fail(f"{path.name}: no Unity version string to retarget")
    data[index + 1:index + 5] = (13).to_bytes(4, "little")
    path.write_bytes(data)


def register_patches(image: Path, asm: Path, entrypoints: Path) -> None:
    names = [name for name in ("SilksongPatches.dll", "0Harmony.dll", "BepInEx.dll") if (asm / name).is_file()]
    scripting = image / "ScriptingAssemblies.json"
    if scripting.is_file():
        payload = json.loads(scripting.read_text(encoding="utf-8"))
        listed = payload.get("names", [])
        types = payload.get("types")
        for name in names:
            if name not in listed:
                listed.append(name)
                if isinstance(types, list):
                    types.append(16)
        scripting.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")

    loads_path = image / "RuntimeInitializeOnLoads.json"
    if not loads_path.is_file():
        return
    payload = json.loads(loads_path.read_text(encoding="utf-8"))
    rows = payload["root"]

    def add(assembly: str, namespace: str, klass: str, method: str, load_types: int) -> None:
        if any(
            row.get("assemblyName") == assembly
            and row.get("className") == klass
            and row.get("methodName") == method
            for row in rows
        ):
            return
        rows.append({
            "assemblyName": assembly, "nameSpace": namespace, "className": klass,
            "methodName": method, "loadTypes": load_types, "isUnityClass": False,
        })

    entries = json.loads(entrypoints.read_text(encoding="utf-8"))["entryPoints"]
    for entry in entries:
        add(
            "SilksongPatches", entry.get("nameSpace", ""), entry["className"],
            entry["methodName"], int(entry["loadTypes"]),
        )
    if "BepInEx.dll" in names:
        add("BepInEx", "BepInEx.Bootstrap", "Chainloader", "Start", 2)
    loads_path.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")


def build_player_image(
    repo: Path,
    unity: Path,
    depot_data: Path,
    root: Path,
    converted: Path,
    asm: Path,
    graphics_api: str = "vulkan",
    texture_patches: Path | None = None,
) -> Path:
    surgery = repo / "tools/bundle-surgery/bin/Release/net8.0/BundleSurgery.dll"
    image = root / "image"
    catalog_out = root / "aa"
    shutil.rmtree(image, ignore_errors=True)
    shutil.rmtree(catalog_out, ignore_errors=True)
    (image / "Resources").mkdir(parents=True)
    (image / "Managed/Metadata").mkdir(parents=True)
    (image / "Managed/Resources").mkdir(parents=True)

    shader_command = "extract-gles3-android" if graphics_api == "gles3" else "extract-vulkan-android"
    for name in ("globalgamemanagers.assets", "resources.assets", "sharedassets0.assets"):
        source = depot_data / name
        if source.is_file():
            run(["dotnet", str(surgery), shader_command, str(source), str(image / name)])
    for name in (
        "globalgamemanagers", "level0", "boot.config", "RuntimeInitializeOnLoads.json",
        "ScriptingAssemblies.json", "app.info", "globalgamemanagers.assets.resS", "resources.assets.resS",
    ):
        source = depot_data / name
        if source.is_file():
            shutil.copy2(source, image / name)
    builtin = depot_data / "Resources/unity_builtin_extra"
    if builtin.is_file():
        shutil.copy2(builtin, image / "Resources/unity_builtin_extra")
    engine_resources = find_android_file(unity / "android", "Data/Resources/unity default resources")
    shutil.copy2(engine_resources, image / "Resources/unity default resources")

    metadata = converted / "Metadata/global-metadata.dat"
    shutil.copy2(metadata, image / "Managed/Metadata/global-metadata.dat")
    resources = converted / "Resources"
    if resources.is_dir():
        for path in resources.iterdir():
            if path.is_file() and path.suffix == ".dat":
                shutil.copy2(path, image / "Managed/Resources" / path.name)

    serialized = (
        "globalgamemanagers", "level0", "globalgamemanagers.assets",
        "resources.assets", "sharedassets0.assets",
    )
    for name in serialized:
        path = image / name
        if path.is_file():
            run(["dotnet", str(surgery), "set-unity-version", str(path), UNITY_VERSION])
            retarget_serialized(path)
    if (image / "Resources/unity_builtin_extra").is_file():
        path = image / "Resources/unity_builtin_extra"
        run(["dotnet", str(surgery), "set-unity-version", str(path), UNITY_VERSION])
        retarget_serialized(path)

    # The player image's own serialized files (resources.assets and friends)
    # carry textures too. Their patches are applied here, on the PC's staged
    # copies, so the device never touches them; the Addressables bundles'
    # patches travel in the same pack and are applied by the device retarget.
    if texture_patches is not None:
        run(["dotnet", str(surgery), "apply-texture-patches", str(image), str(texture_patches)])

    ggm = image / "globalgamemanagers"
    run(["dotnet", str(surgery), "set-graphics-apis", str(ggm), GRAPHICS_APIS[graphics_api]])
    run(["dotnet", str(surgery), "set-build-version", str(ggm), UNITY_VERSION])
    boot = image / "boot.config"
    if boot.is_file():
        lines = [line for line in boot.read_text(encoding="utf-8").splitlines() if not line.startswith("scripting-backend=")]
        boot.write_text("\n".join(lines + ["scripting-backend=il2cpp"]) + "\n", encoding="utf-8")

    entrypoints = repo / "tools/silksong-patches/entrypoints.json"
    register_patches(image, asm, entrypoints)
    seed = sha256(metadata)
    guid = hashlib.md5(seed.encode("ascii")).hexdigest()
    (image / "unity_app_guid").write_text(
        f"{guid[:8]}-{guid[8:12]}-{guid[12:16]}-{guid[16:20]}-{guid[20:]}", encoding="utf-8",
    )

    aa_source = depot_data / "StreamingAssets/aa"
    catalog = aa_source / "catalog.bin"
    if catalog.is_file():
        catalog_out.mkdir(parents=True)
        shutil.copy2(catalog, catalog_out / "catalog.bin")
        if (aa_source / "settings.json").is_file():
            shutil.copy2(aa_source / "settings.json", catalog_out / "settings.json")
        run([
            "dotnet", str(surgery), "patch-catalog-path", str(catalog_out / "catalog.bin"),
            str(catalog_out / "catalog.bin"), CONTENT_ROOT,
        ])

    data_apk = root / "data.apk"
    part = root / "data.apk.part"
    part.unlink(missing_ok=True)
    with zipfile.ZipFile(part, "w", compression=zipfile.ZIP_STORED, allowZip64=False) as archive:
        for directory, prefix in ((image, "assets/bin/Data"), (catalog_out, "assets/aa")):
            if not directory.is_dir():
                continue
            for path in sorted(p for p in directory.rglob("*") if p.is_file()):
                archive.write(path, f"{prefix}/{path.relative_to(directory).as_posix()}")
    part.replace(data_apk)
    note(f"player image packed ({data_apk.stat().st_size // (1024 * 1024)} MB)")
    return data_apk


def dex_player(unity: Path, root: Path) -> Path:
    source = find_android_file(unity / "android", "Variations/il2cpp/Release/Classes/classes.jar")
    output = root / "unity-classes.jar"
    if output.is_file() and output.stat().st_mtime_ns >= source.stat().st_mtime_ns:
        return output
    output.unlink(missing_ok=True)
    d8 = Path(os.environ.get("ANDROID_HOME", "/opt/android-sdk")) / "build-tools/36.0.0/d8"
    run([str(d8), "--release", "--min-api", "26", "--output", str(output), str(source)])
    if not output.is_file() or output.stat().st_size == 0:
        fail("d8 produced no Unity player dex")
    return output


def gles_patch_fingerprint(aa: Path) -> str:
    """Fast cache identity for the large Addressables shader walk."""
    digest = hashlib.sha256()
    digest.update(GLES_PATCH_CONTRACT.encode("ascii"))
    digest.update(b"\0")
    for path in sorted(aa.rglob("*.bundle"), key=lambda p: p.relative_to(aa).as_posix()):
        stat = path.stat()
        digest.update(path.relative_to(aa).as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update(str(stat.st_size).encode("ascii"))
        digest.update(b"/")
        digest.update(str(stat.st_mtime_ns).encode("ascii"))
        digest.update(b"\0")
    return digest.hexdigest()


def build_gles_shader_patches(repo: Path, depot_data: Path, root: Path) -> Path:
    surgery = repo / "tools/bundle-surgery/bin/Release/net8.0/BundleSurgery.dll"
    aa = depot_data / "StreamingAssets/aa"
    if not aa.is_dir():
        fail(f"the depot has no Addressables tree at {aa}")
    output = root / "gles-shaders.zip"
    receipt = root / "gles-shaders.stamp"
    expected = gles_patch_fingerprint(aa)
    if output.is_file() and receipt.is_file() and receipt.read_text(encoding="ascii").strip() == expected:
        note("reusing verified OpenGL ES Addressables shader patches")
        run(["dotnet", str(surgery), "audit-gles3-patches", str(output)])
        return output

    env = os.environ.copy()
    env["SILKSONG_GLES_SHADER_CACHE"] = str(root / "gles-program-cache")
    note("translating all Addressables shaders to OpenGL ES 3.1")
    run(["dotnet", str(surgery), "build-gles3-patches", str(aa), str(output)], env=env)
    run(["dotnet", str(surgery), "audit-gles3-patches", str(output)])
    receipt.write_text(expected + "\n", encoding="ascii")
    return output


def texture_patch_fingerprint(data: Path) -> str:
    """Cache identity for the texture walk: every bundle and serialized file, by stat, plus the contract."""
    digest = hashlib.sha256()
    digest.update(TEXTURE_PATCH_CONTRACT.encode("ascii"))
    digest.update(b"\0")
    files = [p for p in data.rglob("*") if p.is_file() and (p.suffix == ".bundle" or p.suffix == ".assets" or p.suffix == "" or p.suffix == ".resS")]
    for path in sorted(files, key=lambda p: p.relative_to(data).as_posix()):
        stat = path.stat()
        digest.update(path.relative_to(data).as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update(str(stat.st_size).encode("ascii"))
        digest.update(b"/")
        digest.update(str(stat.st_mtime_ns).encode("ascii"))
        digest.update(b"\0")
    return digest.hexdigest()


def build_texture_patches(repo: Path, depot_data: Path, root: Path) -> Path:
    """Re-encode every convertible DXT texture as same-size ETC2 into a patch pack, cached by depot identity."""
    surgery = repo / "tools/bundle-surgery/bin/Release/net8.0/BundleSurgery.dll"
    output = root / "texture-patches.zip"
    receipt = root / "texture-patches.fingerprint"
    expected = texture_patch_fingerprint(depot_data)
    if output.is_file() and receipt.is_file() and receipt.read_text(encoding="ascii").strip() == expected:
        note("reusing verified ETC2 texture patches")
        run(["dotnet", str(surgery), "audit-texture-patches", str(output)])
        return output
    output.unlink(missing_ok=True)
    receipt.unlink(missing_ok=True)
    note("re-encoding DXT textures as same-size ETC2 (this walks the whole depot)")
    run(["dotnet", str(surgery), "build-texture-patches", str(depot_data), str(output)])
    run(["dotnet", str(surgery), "audit-texture-patches", str(output)])
    receipt.write_text(expected + "\n", encoding="ascii")
    return output


def texture_patch_summary(pack: Path) -> dict[str, str]:
    """The manifest fields the import bundle records about a texture pack."""
    with zipfile.ZipFile(pack) as archive:
        raw = archive.read("manifest.json")
    manifest = json.loads(raw)
    if manifest.get("contract") != TEXTURE_PATCH_CONTRACT:
        fail(f"texture pack contract {manifest.get('contract')} is not {TEXTURE_PATCH_CONTRACT}")
    resident = int(manifest.get("androidResidentBytes", 0))
    etc2 = int(manifest.get("etc2Bytes", 0))
    return {
        "textureFormat": "etc2",
        "texturePatchContract": TEXTURE_PATCH_CONTRACT,
        "textureEncoder": str(manifest.get("encoder", "")),
        "textureConvertedCount": str(manifest.get("textureCount", 0)),
        "textureSourceFormats": ",".join(f"{k}:{v}" for k, v in sorted(manifest.get("sourceFormats", {}).items())),
        "textureSkipped": ",".join(f"{k}:{v}" for k, v in sorted(manifest.get("skipped", {}).items())) or "none",
        "textureRgba32FallbackBytes": str(resident),
        "textureEtc2Bytes": str(etc2),
        "textureTheoreticalSavingBytes": str(max(0, resident - etc2)),
        "textureManifestSha256": hashlib.sha256(raw).hexdigest(),
    }


def texture_report(repo: Path, data: Path, output_dir: Path, scan_payloads: bool = True) -> Path:
    """Read-only: every Texture2D in the depot, its format and what it costs on Android.

    Runs bundle-surgery's texture-report over the depot's *_Data directory,
    which holds both the top-level player files and the Addressables tree.
    Nothing under the depot is written; the JSON lands beside the build
    outputs so it can be attached to an issue. The figures are capacity
    figures for everything the game ships, not what any one scene has
    resident -- the runtime TextureFormatProbe answers that from the device.
    """
    surgery = repo / "tools/bundle-surgery/bin/Release/net8.0/BundleSurgery.dll"
    output_dir.mkdir(parents=True, exist_ok=True)
    report = output_dir / "texture-report.json"
    run(texture_report_argv(surgery, data, report, scan_payloads))
    if not report.is_file():
        fail(f"texture-report produced no {report}")
    return report


def texture_report_argv(surgery: Path, data: Path, report: Path, scan_payloads: bool) -> list[str]:
    """The exact command texture_report runs, for tests and for the log."""
    argv = ["dotnet", str(surgery), "texture-report", str(data), str(report)]
    if not scan_payloads:
        argv.append("--skip-payload-scan")
    return argv


def write_bundle(
    output_dir: Path,
    version: str,
    signature: str,
    depot_digest: str,
    payloads: dict[str, Path],
    graphics_api: str = "vulkan",
    texture_fields: dict[str, str] | None = None,
) -> Path:
    manifest = {
        "format": "1", "package": PACKAGE, "unityVersion": UNITY_VERSION,
        "pcBuildContract": PC_BUILD_CONTRACT,
        "graphicsApi": graphics_api,
        "textureFormat": "native",
        "launcherSignature": signature, "depotFingerprint": depot_digest,
        "versionName": version, "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    if texture_fields:
        manifest.update(texture_fields)
        if "texture-patches.zip" not in payloads:
            fail("an ETC2 build must carry texture-patches.zip")
    for name, path in payloads.items():
        key = name.replace("-", "_")
        manifest[f"{key}.size"] = str(path.stat().st_size)
        manifest[f"{key}.sha256"] = sha256(path)
    manifest_text = "".join(f"{key}={value}\n" for key, value in manifest.items())

    output_dir.mkdir(parents=True, exist_ok=True)
    flavor = "-OpenGLES3" if graphics_api == "gles3" else ""
    if manifest["textureFormat"] == "etc2":
        flavor += "-ETC2"
    final = output_dir / f"SilksongAndroid-{version}{flavor}-PC-Build.zip"
    part = output_dir / (final.name + ".part")
    part.unlink(missing_ok=True)
    note("packing the import bundle")
    with zipfile.ZipFile(part, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as archive:
        archive.writestr("manifest.properties", manifest_text)
        for name, path in payloads.items():
            archive.write(path, f"payload/{name}")
    part.replace(final)
    return final


def verify_bundle_payloads(bundle: Path, payloads: dict[str, Path]) -> None:
    """Prove the final ZIP still contains the exact files whose hashes it declares."""
    with zipfile.ZipFile(bundle) as archive:
        properties = {}
        for line in archive.read("manifest.properties").decode("ascii").splitlines():
            if "=" in line:
                key, value = line.split("=", 1)
                properties[key] = value
        for name, built in payloads.items():
            expected = sha256(built)
            declared = properties.get(f"{name.replace('-', '_')}.sha256")
            digest = hashlib.sha256()
            with archive.open(f"payload/{name}") as stream:
                for chunk in iter(lambda: stream.read(1 << 20), b""):
                    digest.update(chunk)
            archived = digest.hexdigest()
            if declared != expected or archived != expected:
                fail(
                    f"signed bundle payload mismatch for {name}: "
                    f"built={expected}, manifest={declared}, zip={archived}"
                )
    note(f"signed ZIP libil2cpp SHA-256: {sha256(payloads['libil2cpp.so'])}")


def sign_bundle(bundle: Path, keystore: Path, storepass: str, keypass: str, alias: str) -> None:
    """Sign every bundle entry with the same certificate as the launcher APK."""
    if not keystore.is_file():
        fail(f"the APK signing keystore is missing: {keystore}")
    if not alias:
        fail("the signing key alias is empty")
    note("signing the import bundle with the APK identity")
    run([
        "jarsigner", "-keystore", str(keystore),
        "-storepass", storepass, "-keypass", keypass,
        "-sigalg", "SHA256withRSA", "-digestalg", "SHA-256",
        str(bundle), alias,
    ])
    # Do not use -strict: a deliberately self-signed local key is valid here
    # but strict mode returns a warning exit code for it.
    run(["jarsigner", "-verify", str(bundle)])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--depot", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    # Everything below is what a game build needs and a texture report does
    # not. They stay required for a build; the check is after parsing so the
    # report mode can leave them out.
    parser.add_argument("--unity", type=Path)
    parser.add_argument("--cache", type=Path)
    parser.add_argument("--jobs", type=int, default=automatic_jobs())
    parser.add_argument("--keystore", type=Path)
    parser.add_argument("--storepass")
    parser.add_argument("--keypass")
    parser.add_argument("--key-alias")
    parser.add_argument("--graphics-api", choices=tuple(GRAPHICS_APIS), default="vulkan")
    parser.add_argument(
        "--texture-format", choices=TEXTURE_FORMATS, default="native",
        help="etc2: re-encode the depot's DXT textures as same-size ETC2 (opt-in; needs the device to apply the pack)",
    )
    parser.add_argument(
        "--texture-report", action="store_true",
        help="only write <output>/texture-report.json for the depot; builds nothing",
    )
    parser.add_argument(
        "--skip-payload-scan", action="store_true",
        help="with --texture-report: do not read texture payloads (faster; no DXT1 alpha detection)",
    )
    args = parser.parse_args()
    if args.jobs < 1 or args.jobs > 64:
        fail("--jobs must be between 1 and 64")

    repo = args.repo.resolve()
    depot = args.depot.resolve()
    data = find_depot_data(depot)
    if not any((data.parent / name).is_file() for name in ("UnityPlayer.so", "Hollow Knight Silksong")):
        fail(f"{data.parent} does not look like the Linux depot (UnityPlayer.so is missing)")
    if (data.parent / "UnityPlayer.dll").is_file():
        fail("the Windows depot cannot be ported; download Steam depot 1030303 for Linux")

    if args.texture_report:
        report = texture_report(repo, data, args.output.resolve(), scan_payloads=not args.skip_payload_scan)
        note("texture report complete")
        note(f"JSON: {report}")
        note("These are capacity figures for the whole depot, not the textures resident in any scene.")
        return 0

    missing = [name for name in ("unity", "cache", "keystore", "storepass", "keypass", "key_alias")
               if getattr(args, name) is None]
    if missing:
        parser.error("a game build needs --" + ", --".join(m.replace("_", "-") for m in missing))
    unity = args.unity.resolve()
    root = args.cache.resolve() / "game-build"
    root.mkdir(parents=True, exist_ok=True)

    version = (repo / "VERSION").read_text(encoding="utf-8").strip()
    csc = ensure_roslyn(args.cache.resolve())
    packages = compile_packages(repo, unity, data, root, csc)
    asm = stage_assemblies(unity, data, packages, root)
    _, converted = convert(repo, unity, root, asm, args.jobs)
    libil2cpp = compile_native(repo, unity, root, args.jobs)
    texture_patches = build_texture_patches(repo, data, root) if args.texture_format == "etc2" else None
    data_apk = build_player_image(repo, unity, data, root, converted, asm, args.graphics_api, texture_patches)
    classes = dex_player(unity, root)
    libunity = find_android_file(unity / "android", "Variations/il2cpp/Release/Libs/arm64-v8a/libunity.so")
    libmain = find_android_file(unity / "android", "Variations/il2cpp/Release/Libs/arm64-v8a/libmain.so")

    signature = launcher_signature(repo / "src/SilksongLauncher.Launcher/app/src/main/assets/ondevice")
    payloads = {
        "libil2cpp.so": libil2cpp,
        "libunity.so": libunity,
        "libmain.so": libmain,
        "data.apk": data_apk,
        "classes.jar": classes,
    }
    if args.graphics_api == "gles3":
        payloads["gles-shaders.zip"] = build_gles_shader_patches(repo, data, root)
    texture_fields = None
    if texture_patches is not None:
        payloads["texture-patches.zip"] = texture_patches
        texture_fields = texture_patch_summary(texture_patches)
    bundle = write_bundle(
        args.output.resolve(), version, signature, depot_fingerprint(data), payloads,
        args.graphics_api, texture_fields,
    )
    sign_bundle(bundle, args.keystore.resolve(), args.storepass, args.keypass, args.key_alias)
    verify_bundle_payloads(bundle, payloads)
    apk = repo / "build" / f"SilksongAndroid-{version}.apk"
    if not apk.is_file():
        fail(f"matching launcher APK is missing: {apk}")
    shutil.copy2(apk, args.output.resolve() / apk.name)

    note("complete")
    note(f"APK:    {args.output.resolve() / apk.name}")
    note(f"Bundle: {bundle}")
    note(f"Graphics: {args.graphics_api}; textures: {args.texture_format}")
    if texture_fields is not None:
        mib = 1024 * 1024
        note(
            f"Textures: {texture_fields['textureConvertedCount']} DXT texture(s) re-encoded as ETC2 "
            f"({texture_fields['textureSourceFormats']}); skipped {texture_fields['textureSkipped']}"
        )
        note(
            f"Textures: RGBA32 fallback {int(texture_fields['textureRgba32FallbackBytes']) // mib} MiB -> "
            f"ETC2 {int(texture_fields['textureEtc2Bytes']) // mib} MiB, theoretical saving "
            f"{int(texture_fields['textureTheoreticalSavingBytes']) // mib} MiB (whole depot, not one scene)"
        )
        note(f"Textures: encoder {texture_fields['textureEncoder']}, manifest sha256 {texture_fields['textureManifestSha256']}")
    note("Install that APK, keep the Linux depot on the device, then choose Import PC build.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as error:
        fail(f"command failed with exit code {error.returncode}: {' '.join(error.cmd[:3])}")
