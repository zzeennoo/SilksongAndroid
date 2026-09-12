package dev.silksong.launcher

import org.junit.Assert.assertEquals
import org.junit.Test

class MonoRuntimeBudgetTest {
    private val gib = 1024L * 1024L * 1024L

    private fun gb(value: Double): Long = (value * gib).toLong()

    @Test fun airMiniStartsAtTheSafeFloor() {
        assertEquals(
            MonoRuntime.Budget(1, 384),
            MonoRuntime.budgetForMemory(gb(3.0), gb(1.8), false, 8),
        )
    }

    @Test fun constrainedS23UsesCurrentHeadroom() {
        assertEquals(
            MonoRuntime.Budget(1, 384),
            MonoRuntime.budgetForMemory(gb(6.9), gb(1.7), false, 8),
        )
        assertEquals(
            MonoRuntime.Budget(2, 512),
            MonoRuntime.budgetForMemory(gb(6.9), gb(4.0), false, 8),
        )
    }

    @Test fun roomyDeviceKeepsItsUnrestrictedProfile() {
        assertEquals(
            MonoRuntime.Budget(8, 0),
            MonoRuntime.budgetForMemory(gb(12.0), gb(8.0), false, 8),
        )
    }

    @Test fun androidLowMemorySignalAlwaysWins() {
        assertEquals(
            MonoRuntime.Budget(1, 384),
            MonoRuntime.budgetForMemory(gb(12.0), gb(8.0), true, 8),
        )
    }

    @Test fun retryAndIndependentLimitsNeverLoosenABudget() {
        assertEquals(
            MonoRuntime.Budget(1, 384),
            MonoRuntime.Budget(2, 512).constrainedBy(MonoRuntime.Budget(1, 384)),
        )
        assertEquals(
            MonoRuntime.Budget(2, 0),
            MonoRuntime.Budget(8, 0).constrainedBy(MonoRuntime.Budget(2, 0)),
        )
        assertEquals(
            MonoRuntime.Budget(1, 384),
            MonoRuntime.Budget(2, 512).tighter(),
        )
        assertEquals(
            MonoRuntime.Budget(1, 384),
            Il2cppConverter.recoveryBudget(
                MonoRuntime.Budget(2, 512),
                MonoRuntime.Budget(2, 512),
            ),
        )
        assertEquals(
            MonoRuntime.Budget(1, 384),
            Il2cppConverter.recoveryBudget(
                MonoRuntime.Budget(2, 512),
                MonoRuntime.Budget(1, 384),
            ),
        )
    }

    @Test fun environmentKeepsTheCoreLimitAtEveryTier() {
        val safe = MonoRuntime.Budget(1, 384).toEnv()
        assertEquals("1", safe["DOTNET_PROCESSOR_COUNT"])
        assertEquals(
            "major=marksweep,nursery-size=1m,soft-heap-limit=384m",
            safe["MONO_GC_PARAMS"],
        )

        val roomy = MonoRuntime.Budget(8, 0).toEnv()
        assertEquals("8", roomy["DOTNET_PROCESSOR_COUNT"])
        assertEquals(null, roomy["MONO_GC_PARAMS"])
    }
}
