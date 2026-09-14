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
    private const val PC_BUILD_CONTRACT = "android-full-system-io-v1"
    private const val MANIFEST = "manifest.properties"
    private const val MAX_BUNDLE_BYTES = 1_500L * 1024L * 1024L
    private val DIGEST = Regex("[0-9a-f]{64}")

    private data class Payload(
        val name: String,
        val maximum: Long,
    )

    private val payloads = listOf(
        Payload("libil2cpp.so", 700L * 1024L * 1024L),
        Payload("libunity.so", 100L * 1024L * 1024L),
        Payload("libmain.so", 10L * 1024L * 1024L),
        Payload("data.apk", 300L * 1024L * 1024L),
        Payload("classes.jar", 20L * 1024L * 1024L),
    )

    data class Staged(
        val directory: File,
        val createdUtc: String,
        val runtimeDigest: String,
    )

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
     * and its depot identity have passed. Only the five known entries are ever
     * extracted, so path traversal and unrelated ZIP contents are inert.
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
                validateManifest(context, properties, depot, launcherSignature)

                for ((index, payload) in payloads.withIndex()) {
                    onProgress("Checking ${payload.name} (${index + 1} of ${payloads.size})")
                    val entry = zip.getJarEntry("payload/${payload.name}")
                        ?: throw IOException("The PC build is missing ${payload.name}")
                    val declared = properties.getProperty("${payload.name}.size")?.toLongOrNull()
                        ?: throw IOException("The PC build has no size for ${payload.name}")
                    if (declared !in 1..payload.maximum || entry.size != declared) {
                        throw IOException("${payload.name} has an invalid size")
                    }
                    val expected = properties.getProperty("${payload.name}.sha256").orEmpty()
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
                validatePayloads(staged)
                return Staged(
                    staged,
                    properties.getProperty("createdUtc").orEmpty(),
                    properties.getProperty("libil2cpp.so.sha256").orEmpty(),
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

    private fun validateManifest(
        context: Context,
        properties: Properties,
        depot: File,
        launcherSignature: String,
    ) {
        if (properties.getProperty("format") != FORMAT) {
            throw IOException("This PC build uses an unsupported format")
        }
        if (properties.getProperty("package") != context.packageName) {
            throw IOException("This PC build is for a different Android package")
        }
        if (properties.getProperty("unityVersion") != UnityFetcher.UNITY_VERSION) {
            throw IOException("This PC build targets a different Unity version")
        }
        if (properties.getProperty("pcBuildContract") != PC_BUILD_CONTRACT) {
            throw IOException(
                "This PC build predates the Android System.IO runtime check. " +
                    "Run Build-On-Windows again and import the new ZIP.",
            )
        }
        if (properties.getProperty("launcherSignature") != launcherSignature) {
            throw IOException("The APK and PC build do not match. Install the APK produced beside this ZIP.")
        }
        val expectedDepot = properties.getProperty("depotFingerprint").orEmpty()
        if (!DIGEST.matches(expectedDepot) || depotFingerprint(depot) != expectedDepot) {
            throw IOException("The PC build was made from a different Silksong depot")
        }
    }

    private fun validatePayloads(staged: File) {
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
        BuildInstallation.writeAtomic(
            File(pkgDir, ".pc-build.identity"),
            "createdUtc=${staged.createdUtc}\nlibil2cppSha256=${staged.runtimeDigest}\n",
        )
        staged.directory.deleteRecursively()
        LauncherLog.log(
            "PC build installed: created=${staged.createdUtc.ifEmpty { "unknown" }}, " +
                "libil2cpp=${staged.runtimeDigest.take(16).ifEmpty { "unknown" }}",
        )
    }

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
