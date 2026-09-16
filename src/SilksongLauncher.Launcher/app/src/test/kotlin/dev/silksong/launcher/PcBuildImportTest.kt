package dev.silksong.launcher

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.IOException
import java.util.Properties

class PcBuildImportTest {
    private fun manifest(vararg pairs: Pair<String, String>) = Properties().apply {
        for ((k, v) in pairs) setProperty(k, v)
    }

    private val digest = "a".repeat(64)

    @Test fun nativeBuildsNeedNoTextureFields() {
        val v1 = manifest("pcBuildContract" to "android-graphics-backend-v1", "graphicsApi" to "vulkan")
        assertEquals("vulkan", PcBuildImport.Manifest.graphicsApi(v1))
        assertEquals("native", PcBuildImport.Manifest.textures(v1).format)
        val v2 = manifest("pcBuildContract" to "android-texture-format-v2", "graphicsApi" to "gles3", "textureFormat" to "native")
        assertEquals("gles3", PcBuildImport.Manifest.graphicsApi(v2))
        assertEquals(false, PcBuildImport.Manifest.textures(v2).isEtc2)
    }

    @Test fun etc2BuildsCarryTheirContractAndDigest() {
        val ok = manifest(
            "pcBuildContract" to "android-texture-format-v2", "graphicsApi" to "vulkan",
            "textureFormat" to "etc2", "texturePatchContract" to PcBuildImport.TEXTURE_PATCH_CONTRACT,
            "textureManifestSha256" to digest, "textureConvertedCount" to "1234", "textureEncoder" to "silksong-etc2-1",
        )
        val profile = PcBuildImport.Manifest.textures(ok)
        assertEquals(true, profile.isEtc2)
        assertEquals(1234, profile.convertedCount)
        assertEquals(digest, profile.manifestSha256)
        assertEquals("etc2 (1234 converted, silksong-etc2-1)", profile.summary)
    }

    @Test fun etc2BuildsFromAnotherConverterAreRefused() {
        val stale = manifest(
            "pcBuildContract" to "android-texture-format-v2", "graphicsApi" to "vulkan",
            "textureFormat" to "etc2", "texturePatchContract" to "dxt-to-etc2-same-size-v0/other",
            "textureManifestSha256" to digest, "textureConvertedCount" to "1",
        )
        try {
            PcBuildImport.Manifest.textures(stale)
            throw AssertionError("a foreign texture contract must be refused")
        } catch (expected: IOException) {
            assertNotEquals(-1, expected.message!!.indexOf("different converter"))
        }
        val v1WithTextures = manifest(
            "pcBuildContract" to "android-graphics-backend-v1", "graphicsApi" to "vulkan",
            "textureFormat" to "etc2", "texturePatchContract" to PcBuildImport.TEXTURE_PATCH_CONTRACT,
            "textureManifestSha256" to digest, "textureConvertedCount" to "1",
        )
        try {
            PcBuildImport.Manifest.textures(v1WithTextures)
            throw AssertionError("ETC2 needs the v2 contract")
        } catch (expected: IOException) {
        }
        val unknown = manifest("pcBuildContract" to "android-texture-format-v2", "graphicsApi" to "vulkan", "textureFormat" to "astc")
        try {
            PcBuildImport.Manifest.textures(unknown)
            throw AssertionError("an unknown texture format must be refused")
        } catch (expected: IOException) {
        }
    }

    @get:Rule val temp = TemporaryFolder()

    @Test fun depotFingerprintMatchesTheDesktopFormat() {
        val depot = temp.newFolder("depot")
        val data = File(depot, "Hollow Knight Silksong_Data").apply { mkdirs() }
        File(data, "Managed").mkdirs()
        File(data, "Managed/A.dll").writeText("a")
        File(data, "Managed/Assembly-CSharp.dll").writeText("game")
        File(data, "ScriptingAssemblies.json").writeText("{}")
        File(data, "globalgamemanagers").writeText("ggm")

        val expected = "bfb1ae34242506ff9bfda43670e9aea2047a1d736481b279e3d2ea5762a1fdd1"
        assertEquals(expected, PcBuildImport.depotFingerprint(depot))

        File(data, "Managed/Assembly-CSharp.dll").appendText(" updated")
        assertNotEquals(expected, PcBuildImport.depotFingerprint(depot))
    }
}
