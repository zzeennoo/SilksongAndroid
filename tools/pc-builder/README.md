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
