package dev.silksong.launcher

import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import java.io.BufferedInputStream
import java.io.ByteArrayInputStream
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.Properties
import java.util.jar.JarEntry
import java.util.jar.JarFile
import java.util.zip.ZipFile

/** Imports the private runtime bundle produced by Build-On-Windows.ps1. */
object PcBuildImport {

    private const val FORMAT = "1"
    // v1 bundles predate the texture field and are all native-texture
    // builds; v2 adds textureFormat and the optional texture patch pack.
    // Both are accepted, so a ZIP built before this change still imports.
    private const val PC_BUILD_CONTRACT_V1 = "android-graphics-backend-v1"
    private const val PC_BUILD_CONTRACT_V2 = "android-texture-format-v2"
    private const val MANIFEST = "manifest.properties"
    // A texture pack carries every DXT atlas of the depot re-encoded, which
    // can be several gigabytes; the ZIP limit has to hold that as well as
    // the engine.
    private const val MAX_BUNDLE_BYTES = 8_000L * 1024L * 1024L
    const val GRAPHICS_VULKAN = "vulkan"
    const val GRAPHICS_GLES3 = "gles3"
    const val TEXTURES_NATIVE = "native"
    const val TEXTURES_ETC2 = "etc2"
    private const val GLES_PATCHES = "gles-shaders.zip"
    private const val TEXTURE_PATCHES = "texture-patches.zip"
    // Must match TextureTranscode.Contract in bundle-surgery, which is what
    // applies the pack; a pack from another revision is refused here rather
    // than by the retarget half an hour in.
    const val TEXTURE_PATCH_CONTRACT = "dxt-bc7-to-etc2-same-size-v2/silksong-etc2-1"
    private val DIGEST = Regex("[0-9a-f]{64}")

    private data class Payload(
        val name: String,
        val maximum: Long,
    )

    private val basePayloads = listOf(
        Payload("libil2cpp.so", 700L * 1024L * 1024L),
        Payload("libunity.so", 100L * 1024L * 1024L),
        Payload("libmain.so", 10L * 1024L * 1024L),
        Payload("data.apk", 300L * 1024L * 1024L),
        Payload("classes.jar", 20L * 1024L * 1024L),
    )
    private val glesPayload = Payload(GLES_PATCHES, 2_000L * 1024L * 1024L)
    private val texturePayload = Payload(TEXTURE_PATCHES, 6_000L * 1024L * 1024L)

    /** What the manifest says about textures; [Manifest.parse] is the pure part, for tests. */
    data class TextureProfile(
        val format: String,
        val convertedCount: Int,
        val encoder: String,
        val manifestSha256: String,
    ) {
        val isEtc2 get() = format == TEXTURES_ETC2
        val summary get() = if (isEtc2) "etc2 ($convertedCount converted, $encoder)" else "native"
    }

    data class Staged(
        val directory: File,
        val createdUtc: String,
        val runtimeDigest: String,
        val graphicsApi: String,
        val textures: TextureProfile = TextureProfile(TEXTURES_NATIVE, 0, "", ""),
    )

    /** The manifest fields this importer reads, validated without any file access. */
    internal object Manifest {
        fun graphicsApi(properties: Properties): String {
            val contract = properties.getProperty("pcBuildContract")
            if (contract != PC_BUILD_CONTRACT_V1 && contract != PC_BUILD_CONTRACT_V2) {
                throw IOException(
                    "This PC build predates the selectable graphics backend. " +
                        "Run Build-On-Windows again and import the new ZIP.",
                )
            }
            val graphicsApi = properties.getProperty("graphicsApi").orEmpty()
            if (graphicsApi != GRAPHICS_VULKAN && graphicsApi != GRAPHICS_GLES3) {
                throw IOException("This PC build has an unsupported graphics backend")
            }
            return graphicsApi
        }

        fun textures(properties: Properties): TextureProfile {
            val format = properties.getProperty("textureFormat", TEXTURES_NATIVE)
            if (format == TEXTURES_NATIVE) return TextureProfile(TEXTURES_NATIVE, 0, "", "")
            if (format != TEXTURES_ETC2) throw IOException("This PC build has an unsupported texture format: $format")
            if (properties.getProperty("pcBuildContract") != PC_BUILD_CONTRACT_V2) {
                throw IOException("This PC build declares ETC2 textures without the contract that carries them")
            }
            if (properties.getProperty("texturePatchContract") != TEXTURE_PATCH_CONTRACT) {
                throw IOException(
                    "This PC build's texture pack was made by a different converter " +
                        "(${properties.getProperty("texturePatchContract")}); rebuild it with this APK's checkout",
                )
            }
            val digest = properties.getProperty("textureManifestSha256").orEmpty()
            if (!DIGEST.matches(digest)) throw IOException("This PC build has no valid texture manifest digest")
            val count = properties.getProperty("textureConvertedCount")?.toIntOrNull()
                ?: throw IOException("This PC build has no texture count")
            return TextureProfile(TEXTURES_ETC2, count, properties.getProperty("textureEncoder").orEmpty(), digest)
        }
    }

    /** The exact game inputs from which IL2CPP output was generated. */
    fun depotFingerprint(depot: File): String {
        val data = PlayerImage.depotData(depot)
            ?: throw IOException("No Silksong player data was found in $depot")
        val managed = File(data, "Managed")
        val files = managed.listFiles().orEmpty()
            .filter { it.isFile && it.extension == "dll" }
            .toMutableList()
        for (name in listOf("globalgamemanagers", "ScriptingAssemblies.json")) {
            File(data, name).takeIf { it.isFile }?.let { files += it }
        }
        if (files.none { it.name == "Assembly-CSharp.dll" }) {
            throw IOException("The selected depot has no Assembly-CSharp.dll")
        }

        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(1 shl 20)
        for (file in files.sortedBy { it.relativeTo(data).path.replace('\\', '/') }) {
            val relative = file.relativeTo(data).path.replace('\\', '/')
            digest.update(relative.toByteArray(Charsets.UTF_8))
            digest.update(byteArrayOf(0))
            file.inputStream().use { input ->
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    digest.update(buffer, 0, count)
                }
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    /**
     * Copies and verifies every payload into an isolated directory.
     *
     * No installed file is touched until the whole ZIP, its launcher identity,
     * and its depot identity have passed. Only the known base entries and the
     * backend's declared optional entry are ever extracted, so path traversal
     * and unrelated ZIP contents are inert.
     */
    fun stage(
        context: Context,
        uri: Uri,
        depot: File,
        root: File,
        launcherSignature: String,
        onProgress: (String) -> Unit = {},
    ): Staged {
        root.mkdirs()
        val archive = File(root, "pc-build.zip.part")
        archive.delete()
        onProgress("Copying the PC build")
        context.contentResolver.openInputStream(uri)?.use { input ->
            FileOutputStream(archive).use { output ->
                val buffer = ByteArray(1 shl 20)
                var total = 0L
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    total += count
                    if (total > MAX_BUNDLE_BYTES) {
                        throw IOException("The selected ZIP is larger than a PC build bundle")
                    }
                    output.write(buffer, 0, count)
                }
                output.fd.sync()
            }
        } ?: throw IOException("The selected file could not be opened")

        val staged = File(root, "pc-import.part")
        staged.deleteRecursively()
        staged.mkdirs()
        try {
            val appSigners = appSignerDigests(context)
            JarFile(archive, true).use { zip ->
                val manifestEntry = zip.getJarEntry(MANIFEST)
                    ?: throw IOException("This ZIP has no $MANIFEST")
                if (manifestEntry.size !in 1..65_536) {
                    throw IOException("The PC build manifest has an invalid size")
                }
                val manifestBytes = zip.getInputStream(manifestEntry).use { it.readBytes() }
                validateSigner(manifestEntry, appSigners)
                val properties = Properties().apply {
                    ByteArrayInputStream(manifestBytes).use { load(BufferedInputStream(it)) }
                }
                val graphicsApi = validateManifest(context, properties, depot, launcherSignature)
                val textures = Manifest.textures(properties)
                val payloads = basePayloads +
                    (if (graphicsApi == GRAPHICS_GLES3) listOf(glesPayload) else emptyList()) +
                    (if (textures.isEtc2) listOf(texturePayload) else emptyList())

                for ((index, payload) in payloads.withIndex()) {
                    onProgress("Checking ${payload.name} (${index + 1} of ${payloads.size})")
                    val entry = zip.getJarEntry("payload/${payload.name}")
                        ?: throw IOException("The PC build is missing ${payload.name}")
                    val manifestName = payload.name.replace('-', '_')
                    val declared = properties.getProperty("$manifestName.size")?.toLongOrNull()
                        ?: throw IOException("The PC build has no size for ${payload.name}")
                    if (declared !in 1..payload.maximum || entry.size != declared) {
                        throw IOException("${payload.name} has an invalid size")
                    }
                    val expected = properties.getProperty("$manifestName.sha256").orEmpty()
                    if (!DIGEST.matches(expected)) {
                        throw IOException("The PC build has no valid digest for ${payload.name}")
                    }

                    val output = File(staged, payload.name)
                    val digest = MessageDigest.getInstance("SHA-256")
                    zip.getInputStream(entry).use { input ->
                        FileOutputStream(output).use { sink ->
                            val buffer = ByteArray(1 shl 20)
                            var written = 0L
                            while (true) {
                                val count = input.read(buffer)
                                if (count < 0) break
                                written += count
                                if (written > declared) throw IOException("${payload.name} expands past its declared size")
                                digest.update(buffer, 0, count)
                                sink.write(buffer, 0, count)
                            }
                            sink.fd.sync()
                            if (written != declared) throw IOException("${payload.name} is truncated")
                        }
                    }
                    validateSigner(entry, appSigners)
                    val actual = digest.digest().joinToString("") { "%02x".format(it) }
                    if (actual != expected) throw IOException("${payload.name} failed its SHA-256 check")
                }
                validatePayloads(staged, graphicsApi, textures)
                return Staged(
                    staged,
                    properties.getProperty("createdUtc").orEmpty(),
                    properties.getProperty("libil2cpp.so.sha256").orEmpty(),
                    graphicsApi,
                    textures,
                )
            }
        } catch (t: Throwable) {
            staged.deleteRecursively()
            throw t
        } finally {
            archive.delete()
        }
    }

    @Suppress("DEPRECATION")
    private fun appSignerDigests(context: Context): Set<String> {
        val info = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            context.packageManager.getPackageInfo(
                context.packageName, PackageManager.GET_SIGNING_CERTIFICATES,
            )
        } else {
            context.packageManager.getPackageInfo(context.packageName, PackageManager.GET_SIGNATURES)
        }
        val signatures = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            info.signingInfo?.apkContentsSigners.orEmpty()
        } else {
            info.signatures.orEmpty()
        }
        if (signatures.isEmpty()) throw IOException("The APK signing certificate is unavailable")
        return signatures.map { digest(it.toByteArray()) }.toSet()
    }

    /** Every imported byte must be signed by the same certificate as this APK. */
    private fun validateSigner(entry: JarEntry, appSigners: Set<String>) {
        // Jar verification is lazy: certificates appear only after the entry
        // has been read completely. Every caller does that before arriving.
        val signedBy = entry.certificates.orEmpty().map { digest(it.encoded) }.toSet()
        if (signedBy.intersect(appSigners).isEmpty()) {
            throw IOException("The PC build is not signed by this APK. Use the APK produced beside the ZIP.")
        }
    }

    private fun digest(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes)
            .joinToString("") { "%02x".format(it) }

    private fun digest(file: File): String {
        val value = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(1 shl 20)
        file.inputStream().use { input ->
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                value.update(buffer, 0, count)
            }
        }
        return value.digest().joinToString("") { "%02x".format(it) }
    }

    private fun validateManifest(
        context: Context,
        properties: Properties,
        depot: File,
        launcherSignature: String,
    ): String {
        if (properties.getProperty("format") != FORMAT) {
            throw IOException("This PC build uses an unsupported format")
        }
        if (properties.getProperty("package") != context.packageName) {
            throw IOException("This PC build is for a different Android package")
        }
        if (properties.getProperty("unityVersion") != UnityFetcher.UNITY_VERSION) {
            throw IOException("This PC build targets a different Unity version")
        }
        val graphicsApi = Manifest.graphicsApi(properties)
        if (properties.getProperty("launcherSignature") != launcherSignature) {
            throw IOException("The APK and PC build do not match. Install the APK produced beside this ZIP.")
        }
        val expectedDepot = properties.getProperty("depotFingerprint").orEmpty()
        if (!DIGEST.matches(expectedDepot) || depotFingerprint(depot) != expectedDepot) {
            throw IOException("The PC build was made from a different Silksong depot")
        }
        return graphicsApi
    }

    private fun validatePayloads(staged: File, graphicsApi: String, textures: TextureProfile) {
        for (name in listOf("libil2cpp.so", "libunity.so", "libmain.so")) {
            val file = File(staged, name)
            val magic = file.inputStream().use { input ->
                ByteArray(4).also { bytes ->
                    var offset = 0
                    while (offset < bytes.size) {
                        val count = input.read(bytes, offset, bytes.size - offset)
                        if (count < 0) throw IOException("$name is truncated")
                        offset += count
                    }
                }
            }
            if (!magic.contentEquals(byteArrayOf(0x7f, 'E'.code.toByte(), 'L'.code.toByte(), 'F'.code.toByte()))) {
                throw IOException("$name is not an ELF library")
            }
        }
        ZipFile(File(staged, "data.apk")).use { zip ->
            for (name in listOf(
                "assets/bin/Data/globalgamemanagers",
                "assets/bin/Data/Managed/Metadata/global-metadata.dat",
            )) {
                if (zip.getEntry(name) == null) throw IOException("data.apk is missing $name")
            }
        }
        ZipFile(File(staged, "classes.jar")).use { zip ->
            if (zip.getEntry("classes.dex") == null) throw IOException("classes.jar has no classes.dex")
        }
        if (graphicsApi == GRAPHICS_GLES3) {
            ZipFile(File(staged, GLES_PATCHES)).use { zip ->
                if (zip.getEntry("manifest.json") == null) {
                    throw IOException("$GLES_PATCHES has no shader patch manifest")
                }
            }
        }
        if (textures.isEtc2) {
            // The manifest inside the pack is what the retarget trusts; its
            // digest was signed with the ZIP, so prove the copy here is that one.
            ZipFile(File(staged, TEXTURE_PATCHES)).use { zip ->
                val entry = zip.getEntry("manifest.json")
                    ?: throw IOException("$TEXTURE_PATCHES has no texture patch manifest")
                val bytes = zip.getInputStream(entry).use { it.readBytes() }
                if (digest(bytes) != textures.manifestSha256) {
                    throw IOException("$TEXTURE_PATCHES carries a manifest the PC build did not sign")
                }
            }
        }
    }

    /** Installs an already-verified set. Readiness is written separately, last. */
    fun install(context: Context, staged: Staged, pkgDir: File) {
        BuildInstallation.invalidate(pkgDir)
        val engine = File(pkgDir, "lib/arm64")
        for (name in listOf("libil2cpp.so", "libunity.so", "libmain.so")) {
            copyAtomic(File(staged.directory, name), File(engine, name), executable = true)
        }
        copyAtomic(File(staged.directory, "data.apk"), File(pkgDir, "data.apk"))
        copyAtomic(
            File(staged.directory, "classes.jar"),
            File(UnityDex.outputDir(context), "classes.jar"),
        )
        val installedGlesPatches = glesPatch(pkgDir)
        if (staged.graphicsApi == GRAPHICS_GLES3) {
            copyAtomic(File(staged.directory, GLES_PATCHES), installedGlesPatches)
        } else {
            installedGlesPatches.delete()
        }
        val installedTexturePatches = texturePatch(pkgDir)
        if (staged.textures.isEtc2) {
            copyAtomic(File(staged.directory, TEXTURE_PATCHES), installedTexturePatches)
        } else {
            installedTexturePatches.delete()
        }
        // The digest validated while reading the signed ZIP is not enough for
        // the identity log: prove the file in the executable private runtime
        // directory is still that exact payload after the atomic copy.
        val installedRuntime = File(engine, "libil2cpp.so")
        val installedDigest = digest(installedRuntime)
        if (installedDigest != staged.runtimeDigest) {
            BuildInstallation.invalidate(pkgDir)
            throw IOException(
                "Installed libil2cpp.so failed its final SHA-256 check " +
                    "(expected ${staged.runtimeDigest}, got $installedDigest)",
            )
        }
        BuildInstallation.writeAtomic(
            File(pkgDir, ".pc-build.identity"),
            "createdUtc=${staged.createdUtc}\nlibil2cppSha256=$installedDigest\n" +
                "graphicsApi=${staged.graphicsApi}\ntextureFormat=${staged.textures.format}\n" +
                "textureManifestSha256=${staged.textures.manifestSha256}\n",
        )
        staged.directory.deleteRecursively()
        LauncherLog.log(
            "PC build installed: created=${staged.createdUtc.ifEmpty { "unknown" }}, " +
                "graphics=${staged.graphicsApi}, textures=${staged.textures.summary}, libil2cpp=$installedDigest",
        )
    }

    fun glesPatch(pkgDir: File): File = File(pkgDir, GLES_PATCHES)

    /** The installed texture patch pack; absent for a native-texture build. */
    fun texturePatch(pkgDir: File): File = File(pkgDir, TEXTURE_PATCHES)

    private fun copyAtomic(from: File, to: File, executable: Boolean = false) {
        to.parentFile?.mkdirs()
        val part = File(to.parentFile, "${to.name}.part")
        part.delete()
        from.inputStream().use { input ->
            FileOutputStream(part).use { output ->
                input.copyTo(output, 1 shl 20)
                output.fd.sync()
            }
        }
        try {
            Files.move(
                part.toPath(), to.toPath(),
                StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING,
            )
        } catch (t: Throwable) {
            part.delete()
            throw IOException("Could not install ${to.name}", t)
        }
        if (executable) to.setExecutable(true, true)
    }
}
