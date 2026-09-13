#!/usr/bin/env bash
#
# Entrypoint for the APK build container.
#
# Runs the same targets a developer runs on the host, after making the two
# adjustments a bind-mounted checkout needs: the Android module has to be
# present, and Gradle must not be pointed at the host's SDK.
set -euo pipefail

say() { echo "[docker] $*"; }

cd /workspace
MODE="${1:-apk}"

# Reject the common wrong input before downloading Unity or compiling the APK.
# Steam's Windows and Linux depots look similar from the folder name; only the
# Linux player is a supported source for this port.
if [[ "$MODE" == pc ]]; then
    [[ -d /game ]] || { echo "[docker] /game is not mounted" >&2; exit 2; }
    game_assembly=$(find /game -maxdepth 6 -type f -path '*_Data/Managed/Assembly-CSharp.dll' -print -quit)
    [[ -n "$game_assembly" ]] || {
        echo "[docker] no Linux Silksong *_Data/Managed/Assembly-CSharp.dll was found under /game" >&2
        exit 2
    }
    game_root="${game_assembly%/*/Managed/Assembly-CSharp.dll}"
    [[ -f "$game_root/UnityPlayer.so" || -f "$game_root/Hollow Knight Silksong" ]] || {
        echo "[docker] /game does not contain the Linux Silksong player (UnityPlayer.so is missing)" >&2
        exit 2
    }
fi

# Two files in the checkout are gitignored, machine-specific, and describe the
# *host*: local.properties names Unity's bundled SDK with a C:/ path that means
# nothing here (and AGP reads it in preference to ANDROID_HOME), and dev.env
# overrides the path discovery in dev.sh. Both belong to the host and the
# checkout is bind-mounted, so they are moved aside and put back rather than
# overwritten. The trap covers a failed build; docker stop sends SIGTERM,
# which is why that is trapped too.
STASHED=()
restore() {
    local s
    for s in "${STASHED[@]:-}"; do
        [[ -n "$s" && -f "$s.docker-stashed" ]] || continue
        mv -f "$s.docker-stashed" "$s"
    done
}
trap restore EXIT INT TERM
for f in src/SilksongLauncher.Launcher/local.properties tools/depot-to-apk/dev.env; do
    # A container killed outright -- docker kill, or a stop that outran the
    # grace period -- never runs the trap, and leaves the host's file stashed.
    # Recovering here rather than only on the way out means the damage lasts
    # until the next run instead of until someone notices Gradle cannot find
    # the SDK.
    if [[ -f "$f.docker-stashed" && ! -f "$f" ]]; then
        mv -f "$f.docker-stashed" "$f"
        say "recovered $f left stashed by an earlier run"
    fi
    [[ -f "$f" ]] || continue
    mv -f "$f" "$f.docker-stashed"
    STASHED+=("$f")
    say "host $f stashed for the run"
done

# The Android player module: Gradle 8.11, the player classes, and
# UnityPlayerActivity's source. Fetched into a container-owned volume, not
# into the bind-mounted build/ -- this build reads nothing the host produced.
# fetch-unity.sh skips the download once the module is unpacked, so this is a
# no-op on every run after the first.
fetch_unity() {
    local what="${1:-android}"
    if [[ ! -d "$UNITY_PLAYER_ROOT/android/Variations" ]]; then
        say "fetching Unity's Android player module (~642 MB, once per volume)"
    fi
    WHAT="$what" ROOT="$UNITY_PLAYER_ROOT" bash tools/ondevice-il2cpp/fetch-unity.sh
}

if [[ "$MODE" == il2cpp-smoke ]]; then
    # Exercise the first real conversion phase without proprietary game data.
    # Converting mscorlib is enough to run RegisterCorlib/ICallMapping, which
    # is exactly where an incompatible desktop host fails before game code is
    # inspected.
    fetch_unity editor
    smoke=$(mktemp -d)
    mkdir -p "$smoke/cpp" "$smoke/data"
    deploy="$UNITY_PLAYER_ROOT/editor/Editor/Data/il2cpp/build/deploy"
    bcl="$UNITY_PLAYER_ROOT/editor/Editor/Data/MonoBleedingEdge/lib/mono/unityaot-linux"
    chmod +x "$deploy/il2cpp"
    say "smoke-testing Unity's desktop IL2CPP host"
    (
        cd "$deploy"
        ./il2cpp --convert-to-cpp \
            --assembly="$bcl/mscorlib.dll" \
            --generatedcppdir="$smoke/cpp" \
            --data-folder="$smoke/data" \
            --dotnetprofile=unityaot-linux \
            --emit-null-checks \
            --enable-array-bounds-check \
            --static-lib-il2-cpp \
            --jobs=1
    )
    [[ -s "$smoke/data/Metadata/global-metadata.dat" ]] || {
        echo "[docker] IL2CPP smoke conversion produced no metadata" >&2
        exit 2
    }
    find "$smoke/cpp" -type f \( -name '*.cpp' -o -name '*.c' \) -print -quit | grep -q . || {
        echo "[docker] IL2CPP smoke conversion produced no native sources" >&2
        exit 2
    }
    say "desktop IL2CPP smoke conversion passed"
    exit 0
fi

fetch_unity android

# dev.sh discovers this when it is not told; telling it keeps it off both the
# host's Unity install and the host's build/ directory.
export AP="$UNITY_PLAYER_ROOT/android"

# Gradle only copies bundle-surgery into the APK; it does not build it, and
# fails with a bare "bundle-surgery is not built" if it is missing. Always
# built here rather than reused from the host: a Windows-built obj/ names
# paths that do not exist in this container.
build_managed_tools() {
    say "building bundle-surgery"
    dotnet build -c Release tools/bundle-surgery/BundleSurgery.csproj --nologo -v quiet

    # Same for mod-weaver, the build-time Harmony weaver.
    say "building mod-weaver"
    dotnet build -c Release tools/mod-weaver/ModWeaver.csproj --nologo -v quiet
}

build_managed_tools

# A signing key, because a container has none.
#
# apksigner needs one and the build fails at the last step without it. On a
# workstation this file is the one Android Studio generates; here the container
# is fresh every run, so an equivalent is made with the same conventional alias
# and passwords. CI overrides KEYSTORE with a real release key, and this is
# skipped.
#
# The Windows wrapper and compose mount /root/.android as a named volume, so
# this locally generated identity survives later containers and their APKs can
# update each other. It still differs from an APK built elsewhere; official
# releases use a stable key from a repository secret for the same reason.
if [[ -z "${KEYSTORE:-}" && ! -f "$HOME/.android/debug.keystore" ]]; then
    say "generating a debug keystore (none in this container)"
    mkdir -p "$HOME/.android"
    keytool -genkeypair -v \
        -keystore "$HOME/.android/debug.keystore" \
        -storepass android -keypass android \
        -alias androiddebugkey \
        -keyalg RSA -keysize 2048 -validity 10000 \
        -dname "CN=Android Debug, O=Android, C=US" >/dev/null 2>&1
fi

case "$MODE" in
    apk)
        # No adb here: the container has the toolchain but not the USB bus.
        # The APK lands in the bind mount for the host to install.
        #
        # DEBUGGABLE defaults to 1 because the usual reason to run this is a
        # dev build on a machine with no Android SDK, and android:debuggable is
        # what makes `run-as` work. A release must set it to 0 -- CI does.
        say "building the APK"
        INSTALL=0 DEBUGGABLE="${DEBUGGABLE:-1}" bash tools/depot-to-apk/dev.sh
        if [[ "${RUN_TESTS:-0}" == 1 ]]; then
            gradle_jar=$(find "$AP/Tools/gradle/lib" -maxdepth 1 -name 'gradle-launcher-*.jar' | head -1)
            [[ -n "$gradle_jar" ]] || { echo "[docker] Unity Gradle launcher is missing" >&2; exit 2; }
            say "running Android unit tests"
            (
                cd src/SilksongLauncher.Launcher
                java -classpath "$gradle_jar" org.gradle.launcher.GradleMain \
                    :app:testReleaseUnitTest --console=plain --no-daemon
            )
        fi
        ;;
    pc)
        # A private, game-derived build for the depot mounted at /game. The
        # launcher APK is built in the same run so its compatibility signature
        # is guaranteed to match the import bundle.
        mkdir -p /pc-output /pc-cache
        say "fetching the desktop IL2CPP pieces and Input System (~640 MB once)"
        fetch_unity editor
        fetch_unity packages
        say "building the matching launcher APK"
        INSTALL=0 DEBUGGABLE=0 bash tools/depot-to-apk/dev.sh
        say "building the private PC port bundle"
        pc_args=(
            --repo /workspace
            --depot /game
            --unity "$UNITY_PLAYER_ROOT"
            --cache /pc-cache
            --output /pc-output
            --keystore "${KEYSTORE:-$HOME/.android/debug.keystore}"
            --storepass "${KS_PASS:-android}"
            --keypass "${KEY_PASS:-android}"
            --key-alias "${KEY_ALIAS:-androiddebugkey}"
        )
        [[ -z "${PC_BUILD_JOBS:-}" ]] || pc_args+=(--jobs "$PC_BUILD_JOBS")
        python3 tools/pc-builder/build.py "${pc_args[@]}"
        ;;
    shell)
        exec bash
        ;;
    *)
        exec "$@"
        ;;
esac
