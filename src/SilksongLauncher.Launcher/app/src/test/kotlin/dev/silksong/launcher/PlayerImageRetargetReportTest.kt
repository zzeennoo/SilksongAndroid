package dev.silksong.launcher

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PlayerImageRetargetReportTest {
    @Test fun parsesAnInFlightReceipt() {
        val report = PlayerImage.parseRetargetReport(
            """
            total=2068
            processed=25
            changed=16
            skipped=9
            failed=0
            complete=0
            """.trimIndent(),
        )!!
        assertEquals(2068, report.total)
        assertEquals(25, report.processed)
        assertEquals(16, report.changed)
        assertEquals(9, report.skipped)
        assertFalse(report.complete)
    }

    @Test fun parsesACompleteReceipt() {
        val report = PlayerImage.parseRetargetReport(
            "total=2068\nprocessed=2068\nchanged=16\nskipped=2052\nfailed=0\ncomplete=1\n",
        )!!
        assertTrue(report.complete)
        assertEquals(report.total, report.processed)
        assertEquals(report.total, report.changed + report.skipped + report.failed)
    }

    @Test fun rejectsMissingOrNegativeFields() {
        assertNull(PlayerImage.parseRetargetReport("total=10\ncomplete=1\n"))
        assertNull(PlayerImage.parseRetargetReport(
            "total=10\nprocessed=-1\nchanged=0\nskipped=0\nfailed=0\ncomplete=0\n",
        ))
    }
}
