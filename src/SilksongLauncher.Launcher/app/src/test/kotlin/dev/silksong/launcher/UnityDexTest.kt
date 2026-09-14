package dev.silksong.launcher

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class UnityDexTest {
    @get:Rule val temp = TemporaryFolder()

    private open class LoaderBase {
        var added: String? = null

        @Suppress("unused")
        fun addDexPath(path: String) {
            added = path
        }
    }

    private class InheritedLoader : LoaderBase()

    private class TrustAwareLoader {
        var added: Pair<String, Boolean>? = null

        @Suppress("unused")
        fun addDexPath(path: String, trusted: Boolean) {
            added = path to trusted
        }
    }

    private class NoCompatibleLoader

    @Test fun addsTheJarDirectlyThroughTheInheritedLoaderApi() {
        val jar = temp.newFile("classes.jar")
        val loader = InheritedLoader()

        assertTrue(UnityDex.addDexPath(loader, jar))
        assertEquals(jar.absolutePath, loader.added)
    }

    @Test fun supportsTheTrustAwareLoaderApiWithoutTrustingTheJar() {
        val jar = temp.newFile("classes.jar")
        val loader = TrustAwareLoader()

        assertTrue(UnityDex.addDexPath(loader, jar))
        assertEquals(jar.absolutePath to false, loader.added)
    }

    @Test fun refusesToClaimSuccessWhenThePlatformHasNoCompatibleApi() {
        assertFalse(
            UnityDex.addDexPath(
                NoCompatibleLoader(),
                temp.newFile("classes.jar"),
            ),
        )
    }
}
