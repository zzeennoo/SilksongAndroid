package dev.silksong.launcher

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class PcBuildImportTest {
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
