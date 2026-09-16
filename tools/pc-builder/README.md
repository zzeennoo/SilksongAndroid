# PC game builder

This is the private desktop half of the port. It takes a user's **Linux**
Silksong depot, performs the memory-heavy IL2CPP conversion and ARM64 native
compile on the PC, and emits:

- a launcher APK built from the exact same checkout;
- a compressed `PC-Build.zip` imported by that launcher.

The ZIP contains the generated `libil2cpp.so`, player image, stock Unity engine
libraries and dexed player classes. It does **not** contain the depot's 8 GB
Addressables tree. The game continues to read that from the folder selected on
the Android device, after the launcher retargets it in place.

The output is derived from the user's game and is for personal use. Do not
attach it to a GitHub release or redistribute it.

## Windows

Install and start Docker Desktop, then either drag the Linux depot folder onto
`Build-On-Windows.cmd`, or run:

```powershell
.\Build-On-Windows.ps1 -Depot "D:\Games\Silksong-Linux"
```

Outputs land in `pc-output/`. Install the APK, copy the ZIP to the handheld,
keep the ZIP compressed, and choose **Import PC build** on the setup screen.
The launcher validates both the APK build and the device's depot before it
installs anything. The ZIP is signed with the APK's own certificate as well,
so an altered or third-party bundle is rejected before any library is installed.

### Experimental OpenGL ES 3.1 path

Vulkan remains the default. For the 3 GB AYANEO, where the Vulkan/ION path can
drain CMA and be killed even while per-process PSS looks modest, build the
alternate backend explicitly:

```powershell
.\Build-On-Windows.ps1 -Depot "D:\Games\Silksong-Linux" -GraphicsApi OpenGLES3
```

The output name contains `OpenGLES3`. The Linux depot does not contain an
Android GLES shader slice, so changing `m_GraphicsAPIs` alone is invalid. This
build translates every referenced Vulkan SPIR-V program to ESSL 3.10 with a
pinned SPIRV-Cross, validates the result with glslang, and packages the top-
level player shaders plus a deduplicated patch archive for all Addressables
bundles. The archive is signed and hashed like every other PC-build payload.
On import, the AYANEO only installs the converted blobs while retargeting the
bundles; SPIRV-Cross and glslang never run on the device.

On the low-memory profile GLES uses a 60 fps cap and a 540-pixel render short
side. That is 720×540 on the AYANEO's 4:3 window, preserving its shape. Vulkan
keeps the safer 30 fps cap. This is an experimental compatibility path, not a
claim that GLES is normally faster than Vulkan or that every scene will hold
60 fps.

### ETC2 textures (opt-in)

If the texture report shows the depot's atlases as plain DXT1/DXT5, the PC
can re-encode them into ETC2, which every Android GPU samples natively:

```powershell
.\Build-On-Windows.ps1 -Depot "D:\Games\Silksong-Linux" -TextureFormat ETC2
```

(or `ETC2` as an extra argument to `Build-On-Windows.cmd`, combinable with
`OpenGLES3`). The conversion is same-size by construction: DXT1 becomes
ETC2_RGB, or ETC2_RGBA1 where the payload uses one-bit transparency, and DXT5
becomes ETC2_RGBA8, each 8 or 16 bytes per 4x4 block like the format it
replaces. No offset, stream size or `m_CompleteImageSize` changes; only
`m_TextureFormat` and the payload bytes do. Crunched formats, BC4/BC5/BC7,
cubemaps, arrays and any texture whose payload size does not match its
dimensions are left as they are and counted in the build's summary.

The PC applies the player image's own textures directly and ships the
Addressables textures as `texture-patches.zip` inside the PC build, which the
device applies while it retargets the bundles: a byte copy, no encoding.
Every written texture is parsed back and checked against the pack's digest;
a mismatch fails that file rather than leaving it half converted. The pack
can be several gigabytes, since it carries every atlas; the ZIP name gains
`-ETC2`, the manifest records the converter contract, encoder, counts, bytes
and the pack manifest's SHA-256, and the importer refuses a pack from a
different converter revision.

The encoder is this repository's own (`tools/bundle-surgery/Etc2.cs`, from
the Khronos specification; see `NOTICE.md`). It emits ETC1 differential and
individual modes and the planar mode, not T/H modes, so blocks that mix two
unrelated colours inside a 2x4 sub-block lose some fidelity; the visible
part of a sprite on a transparent background is fitted alone and stays
sharp. Output is deterministic and the encoder revision is part of the
contract, so a cached pack is reused only for the same depot and the same
encoder.

### Texture report (read-only)

The Linux depot is a desktop build, so its textures are DXT1/DXT5 (and
possibly BC7). No Mali or Adreno GPU samples those formats; the Android player
expands each one to RGBA32 on the CPU at load time and uploads that -- four
times the bytes of a DXT5 atlas, eight times a DXT1 one, held by the driver
rather than in the process heap. Before anything converts textures, measure
how much of the depot is in that position:

```powershell
.\Build-On-Windows.ps1 -Depot "D:\Games\Silksong-Linux" -TextureReport
```

(or drag the depot onto `Build-On-Windows.cmd` with `TextureReport` as the
second argument). It needs only Docker and the depot: no Unity pieces are
fetched and nothing is built. It writes `pc-output\texture-report.json` and
prints a per-format summary: count, source bytes, what they cost on Android
once expanded, what a same-size ETC2 swap would cost, and the difference. It
also counts what a same-size swap could not handle yet (crunched formats,
BC4/BC5/BC7, cubemaps, arrays, payloads whose size does not match their
dimensions) and lists the largest textures.

Two things the numbers are not. They are **capacity** figures for everything
the game ships, not what the language-select screen has resident; the
`TextureFormatProbe` patch logs the latter from the device, as
`[TextureProbe]` lines in Unity's log, with `SystemInfo.SupportsTextureFormat`
answers for DXT1/DXT5/BC7/ETC2/ASTC and two snapshots of loaded textures by
format. And nothing is converted: the report opens the depot read-only.

Add `-SkipPayloadScan` to skip reading texture bytes. That is faster on a
slow disk but cannot tell which DXT1 textures use one-bit transparency (those
would need `ETC2_RGBA1`, not `ETC2_RGB`), so it reports them as unscanned.

The locally generated APK has one signing identity that is kept in Docker's
`silksong-signing` volume. It can update later APKs made by this same PC, but
it cannot update an APK signed by GitHub or another machine. Android reports
that case only as **App not installed**. If that happens, preserve or recopy
the Linux depot somewhere outside the old app's `Android/data` directory,
uninstall the old app **without** retaining its app data, then install the new
APK. Retained data remains tied to the old signer and can cause the same
failure even after the app looks uninstalled.

The first run is large: Docker, Android SDK/NDK and the pinned Unity components
are downloaded. Docker volumes retain them, the signing key, the generated C++
and native objects. Later runs are incremental and APKs remain installable over
one another because the local signing key is stable.

Before conversion, the builder reports which staged assemblies define the
launch-critical `System.IO` types and rejects competing `File` or `FileStream`
core definitions. (`unityaot-linux` intentionally has private `PathInternal`
implementations in multiple assemblies.) It rewrites every
`PathInternal.GetIsCaseSensitive()` body to the method's conservative fallback,
`false`, avoiding the temporary-`FileStream` probe that aborts on the affected
Android 11 runtime. The generated-source audit requires every copy to be that
exact constant fallback. After linking, the ARM64 ELF audit requires every
`PathInternal` symbol to have no `FileStream` edge. A ZIP is written only when
both proofs pass.

The final build prints `linked libil2cpp SHA-256` and `signed ZIP libil2cpp
SHA-256`; they must be identical. The importer hashes the installed private
copy again and records the full digest in `PC build installed: ...`, so that log
can be compared byte-for-byte with both PC lines and identifies bytes on the
device rather than merely repeating a manifest value.

The fallback patch changes the staged managed assemblies and advances the
conversion contract, so an older generated tree is not reused. Native objects
remain content-addressed and rebuild as their generated translation units
change. No cache or Docker volume needs to be deleted manually.

Use `-Jobs 4` to override the automatic worker count. By default the builder
uses about one worker per 2 GiB available to Docker, capped at eight, so a
normal Docker Desktop configuration does not overcommit memory late in IL2CPP.

## Container interface

The PowerShell wrapper drives this interface directly:

```bash
docker run --rm --platform linux/amd64 \
  -v "$PWD:/workspace" -v "/path/to/depot:/game:ro" \
  -v "$PWD/pc-output:/pc-output" \
  -v silksong-unity-player:/opt/unity-player \
  -v silksong-pc-build:/pc-cache \
  -v silksong-gradle:/gradle \
  -v silksong-nuget:/root/.nuget/packages \
  -v silksong-apk-build:/root/.cache/silksong \
  -v silksong-signing:/root/.android \
  -e PC_BUILD_JOBS=8 silksong-pc-builder:latest pc
```

Add `-e PC_GRAPHICS_API=gles3` for the OpenGL ES build and
`-e PC_TEXTURE_FORMAT=etc2` for ETC2 textures. Replace the trailing
`pc` with `texture-report` (optionally with `-e PC_SKIP_PAYLOAD_SCAN=1`) for
the read-only texture report; only the `/workspace`, `/game` and `/pc-output`
mounts matter to it.
