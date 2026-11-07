package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SlotPolicyTest {
    private fun policy(n: Int, q: StreamQuality = StreamQuality.QHD, cap: Int = SlotPolicy.DEFAULT_BUDGET) =
        SlotPolicy(n, q, cap)

    @Test fun announcedPerSlotCount() {
        assertEquals(AnnouncedMode(2560, 1440, 30), policy(1, StreamQuality.QHD).announced())
        assertEquals(AnnouncedMode(1920, 1080, 30), policy(1, StreamQuality.HD).announced())
        assertEquals(AnnouncedMode(1920, 1080, 30), policy(2, StreamQuality.UHD).announced())
        assertEquals(AnnouncedMode(1920, 1080, 30), policy(2, StreamQuality.HD).announced())
        assertEquals(AnnouncedMode(1280, 720, 30), policy(4, StreamQuality.QHD).announced())
    }

    @Test fun budgetMath() {
        val p = policy(1)
        assertEquals(8100 * 30, p.costOf(1920, 1080))
        assertEquals(3600 * 30, p.costOf(1280, 720))
    }

    @Test fun twoFullHdFit() {
        val p = policy(2)
        assertEquals(Admission.Accepted, p.admit(0, 1920, 1080))
        assertEquals(Admission.Accepted, p.admit(1, 1920, 1080))
    }

    @Test fun fourSevenTwentyFit() {
        val p = policy(4)
        for (i in 0..3) assertEquals(Admission.Accepted, p.admit(i, 1280, 720))
    }

    private fun tight(): SlotPolicy {
        val p = policy(4)
        return SlotPolicy(4, StreamQuality.QHD, p.costOf(1280, 720) * 2)
    }

    @Test fun extraSourceRefused() {
        val p = tight()
        assertEquals(Admission.Accepted, p.admit(0, 1280, 720))
        assertEquals(Admission.Accepted, p.admit(1, 1280, 720))
        assertEquals(Admission.Saturated, p.admit(2, 1280, 720))
        assertTrue(p.isSaturated(2))
        assertFalse(p.isSaturated(0))
        assertFalse(p.isSaturated(1))
    }

    @Test fun releaseFreesSaturated() {
        val p = tight()
        p.admit(0, 1280, 720); p.admit(1, 1280, 720); p.admit(2, 1280, 720)
        assertEquals(listOf(2), p.release(0))
        assertFalse(p.isSaturated(2))
        assertEquals(Admission.Accepted, p.admit(2, 1280, 720))
    }

    @Test fun releaseReturnsSaturatedInArrivalOrder() {
        val p = tight()
        p.admit(0, 1280, 720); p.admit(1, 1280, 720); p.admit(3, 1280, 720); p.admit(2, 1280, 720)
        assertEquals(listOf(3), p.release(0))
        assertTrue(p.isSaturated(2))
    }

    @Test fun readmitSameSlotReplacesCost() {
        val p = tight()
        p.admit(0, 1280, 720)
        assertEquals(Admission.Accepted, p.admit(1, 1280, 720))
        assertEquals(Admission.Accepted, p.admit(0, 1280, 720))
        assertEquals(Admission.Accepted, p.admit(0, 1280, 720))
    }

    @Test fun saturatedSlotNeverCountsInBudget() {
        val p = tight()
        p.admit(0, 1280, 720); p.admit(1, 1280, 720); p.admit(2, 1280, 720); p.admit(3, 1280, 720)
        assertEquals(emptyList<Int>(), p.release(2))
        assertEquals(listOf(3), p.release(1))
    }

    @Test fun releaseUnknownSlotIsNoop() {
        val p = tight()
        p.admit(0, 1280, 720)
        assertEquals(emptyList<Int>(), p.release(3))
        assertEquals(Admission.Accepted, p.admit(1, 1280, 720))
        assertEquals(Admission.Saturated, p.admit(2, 1280, 720))
    }

    @Test fun singleUhdSourceIsAlwaysAdmitted() {
        val p = policy(1, StreamQuality.UHD)
        assertEquals(Admission.Accepted, p.admit(0, 3840, 2160))
        assertFalse(p.isSaturated(0))
    }

    @Test fun messageIsExact() {
        assertEquals("Trop de sources pour cette box", SlotPolicy.MESSAGE_SATURATED)
    }
}
