# Silksong Android

**Hollow Knight: Silksong** Android port with dual-screen capabilites and optional
Steam integration for game files and cloud saves.

<p align="center">
  <img src="docs/icon.png" alt="Silksong Android app icon" width="180" />
  <br />
  <em>Thanks to Kaz Kirigiri for the artwork!</em>
</p>

<img width="2048" height="1536" alt="IMG_1639" src="https://github.com/user-attachments/assets/c9ddb25d-37a8-4e7e-877c-0f13eb13efed" />

## Features

- **Dual screen**: Show interactive map, inventory, crests, tasks and journal on the second screen
- **Steam integration (optional)**: Sign in to Steam if you want the app to download game files and/or your Steam cloud saves for you
- **High performance**: Compiles to native arm64 via IL2CPP and uses Vulkan shaders
- **Fully open source and legal**: Supply your own game files either manually or through Steam sign-in (the app downloads them for you)
- **Two build paths**: Port on the device, or move the memory-heavy conversion and native compile to a Windows PC
- **Mod support (beta)**: BepInEx 5 plugins, woven into the game at build time (see [Mod support](#modsupport))
- **QoL settings**: Skip intro, set resolution, auto upload/download cloud saves etc.
- **Any device**: Any Android device works, single screen as well. Android 13 only for now (Android 15 is not supported at the moment)

## Getting started

1. Download the latest APK from
   [Releases](https://github.com/jakobkhansen/SilksongAndroid/releases/latest)
   and install it.
2. Supply your own game files. Either copy them across from your PC yourself,
   or let the app fetch them for you by signing in to Steam.
3. Press the button. The app fetches everything it needs and builds the game.
   The build takes 20–30 minutes on a roomy Snapdragon 8 Gen 2. Low-memory
   devices automatically use fewer workers and can take considerably longer.
4. Play. After the first build, launching is instant.

Any device should do (tested on the AYN Thor and the Retroid Pocket Flip 2). Open an issue
if your device doesn't work.

The native compile is crash-resumable: completed objects are verified and
reused on the next attempt. IL2CPP conversion still has to finish in one run,
so constrained devices use its lower-peak partial-per-assembly mode and retain
an interrupted-attempt checkpoint for safer retry settings.

### Building on a Windows PC

For a device with 3–4 GB of RAM, the PC path is considerably more reliable.
It needs Docker Desktop and the same Linux depot, but no Unity installation,
Android Studio, JDK, Android SDK or signing tools on Windows:

```powershell
.\Build-On-Windows.ps1 -Depot "D:\Games\Silksong-Linux"
```

You can also drag the depot folder onto `Build-On-Windows.cmd`. The first run
downloads and caches the pinned build tools. It produces a matching APK and a
`PC-Build.zip` in `pc-output/`; install that APK, copy the ZIP to the device,
and choose **Import PC build** without extracting it. Keep the Linux depot on
the device—the ZIP deliberately excludes its 8 GB of game content.

An experimental OpenGL ES 3.1 build is available for devices whose Vulkan
driver exhausts shared graphics memory. It keeps Vulkan as the default for
other devices and targets 60 fps at a 540-pixel render short side on the 3 GB
profile (720×540 on the AYANEO's 4:3 screen):

```powershell
.\Build-On-Windows.ps1 -Depot "D:\Games\Silksong-Linux" -GraphicsApi OpenGLES3
```

`-TextureReport` writes a read-only inventory of the depot's texture formats
and what they cost on an Android GPU, without building anything; see the
[PC builder guide](tools/pc-builder/README.md#texture-report-read-only).

The content rewrite keeps only the selected shader backend. You can move from
an already-retargeted Vulkan tree to OpenGL ES because the PC bundle supplies
the converted GLES blobs. Moving that same tree back to Vulkan requires
restoring the original Linux Addressables content; the launcher rejects the
unsafe direction instead of producing a mixed package.

This route translates and validates every referenced shader on the PC and
puts a deduplicated patch set in the signed import ZIP; the handheld only
applies those blobs. It does not run a shader compiler on-device. The 60 fps
value is a target/cap, not a guarantee—the GLES driver and game workload still
decide the achieved rate.

The first PC-built APK may not install over an APK from another source because
Android requires matching signing keys. If Android says **App not installed**,
see the signing/data-preservation note in the [PC builder guide](tools/pc-builder/README.md)
before uninstalling anything.

The bundle is generated from your copy of the game and is for your own use;
do not upload or redistribute it. See [the PC builder guide](tools/pc-builder/README.md).

### Supplying the game files yourself

If you'd rather not sign in, download the game's **Linux** depot on a PC with
[DepotDownloader](https://github.com/SteamRE/DepotDownloader). The Linux files are the
ones the port is built from; the Windows (1030301) and macOS (1030302) depots will not do,
and the app rejects them:

```sh
DepotDownloader -app 1030300 -depot 1030303 -username <your account> -password <your password> -dir silksong
```

Copy the whole `silksong` folder onto the device, as long as it is on
the device's own storage. Then press **Choose folder** in the app and pick it.

```
silksong/                          <-- pick THIS one
├── Hollow Knight Silksong.x86_64
├── UnityPlayer.so
└── Hollow Knight Silksong_Data/
    ├── Managed/
    ├── MonoBleedingEdge/
    ├── Plugins/
    ├── Resources/
    ├── StreamingAssets/
    ├── globalgamemanagers
    ├── resources.assets
    └── ...
```

**Wherever you put them, leave them there.** The game is several gigabytes of content that
is never copied into the app: it is read from that folder every time you play, and every
app update that rebuilds the game reads it too. Deleting or moving the folder stops the
game from starting. The app leaves a `SILKSONG-DO-NOT-DELETE.txt` in there saying so.

## Mod support (beta)

BepInEx 5 plugins work, with one catch: since the games compiles to C++ code to get
maximum performance, mods need to be compiled too, so installing or removing a mod means
rebuilding. You can still enable/disable mods without rebuilding. 

1. Open **Mods** from the launcher and press **Install a mod from a folder**.
   Pick the folder the mod came in — the whole folder is copied in, so a mod
   that is a plugin plus its own libraries and config needs no sorting out.
2. Say yes when it offers to rebuild, or carry on and rebuild later.
3. Config files appear in `mods/config` after the first launch.

The Mods screen lists everything it found, marks each one **built** or **not
built**, and says what the weaver made of it — how many patches it applied and
every one it could not. Copying DLLs into
`Android/data/com.jakobkhansen.silksong/files/mods` by hand still works and is
picked up the same way.

The rebuild only redoes the conversion and the native compile, and only when
the folder actually changed — and the compile is incremental, so it is a few
minutes rather than the twenty the first build took. Drop a mod in and press
Launch and the launcher offers to rebuild, or to play the build you have.

If a rebuild is interrupted, resume it before playing. Completed work is
reused, but the launcher will not run a partly installed game. Mod build
indicators update only after installation succeeds.

**What does not work**: transpilers, patch targets computed at runtime,
`Reflection.Emit`, and loading a DLL discovered at runtime. There is no IL left
by the time the game runs, so nothing can be patched then. The mods screen
names every patch a plugin could not apply, before the build starts.
Plugins with definite missing APIs or required dependencies are marked failed
and left out, rather than breaking compilation for the other mods.

### The configuration menu

Mods that expose settings expect
[BepInEx's Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager/releases)
to be there to draw them. It works here — download the BepInEx 5 build and
install its folder the same way as any other mod.

Open it with L3+R3 (Same as F1 on PC). That binding lives in `mods/config/BepInEx.cfg` and
is read at startup, so it can be changed to any key or button without rebuilding — and
because it is a BepInEx setting like any other, the menu lists it, under Advanced
settings.

It is not shipped with the app, and neither is BepInEx: mods are your files,
downloaded by you, and the build that compiles them into the game happens on
your device.

### Tested mods

Built and played on device. Links go to the page each was downloaded from.

- [BepInEx 5 + Configuration Manager](https://www.nexusmods.com/hollowknightsilksong/mods/26) — (only needed for configuration menu)
- [AutoMap](https://www.nexusmods.com/hollowknightsilksong/mods/31)
- [SaveScopedConfig](https://www.nexusmods.com/hollowknightsilksong/mods/1123) — required by AutoMap
- [Bonfire Teleport](https://www.nexusmods.com/hollowknightsilksong/mods/156) — (use top-screen full map to teleport)
- [Stakes of Marika – Rebirth Anywhere](https://www.nexusmods.com/hollowknightsilksong/mods/46) — (partial, no custom spawnpoint)
- [Healthbar & Damage Show](https://www.nexusmods.com/hollowknightsilksong/mods/28)

If a mod is not working for you, feel free to open an issue, but I cannot guarantee that
all mods will be supported.

## Steam Cloud Saves

If you sign in to Steam, you also get your Steam Cloud saves. Pull before you play, push
when you're done, or let the launcher do both automatically (see settings). Save conflicts
are handled, but back your saves up if you want to be certain nothing is lost.

Signing in, by QR code or password, goes through
[JavaSteam](https://github.com/Longi94/JavaSteam), an open-source Steam client library.
Your password is never stored: the only thing kept is the login token Steam issues in
return, encrypted, and it never leaves the device.

## AI assistance

Much of this project was written with AI assistance. Everything in it is reviewed and
tested on real hardware before release.

## Legal

This repository and published APK contain **no game content and nothing Unity-made**.
Silksong is © Team Cherry, and none of its code, art or audio is distributed here. The
published APK is a build system: it downloads Unity's toolchain, takes *your* game files
(supplied by hand, or fetched with your own Steam account), and compiles a playable build.
The optional PC builder creates a private, game-derived bundle locally and explicitly
does not make that bundle suitable for publication.

The tooling is MIT-licensed; see [LICENSE](LICENSE). Third-party open-source
components shipped in the APK are listed in [NOTICE.md](NOTICE.md), which also
records what the APK deliberately does *not* contain.


## Building from source

You don't need Unity or any game files to build the APK:

```bash
make player     # once: fetch Unity's Android player module (~642 MB)
make surgery    # once: build bundle-surgery
make dev        # rebuild, repackage, install
```

Requires an Android SDK, JDK 17+ and the .NET 8 SDK; on Windows use Git Bash.
`make docker-apk` does the same in a container. See [COPILOT.md](COPILOT.md)
for the full development loop.
