package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SlotPolicyTest {
    private fun policy(n: Int, q: StreamQuality = StreamQuality.QHD, cap: Int = SlotPolicy.DEFAULT_BUDGET) =
        SlotPolicy(n, q, cap)

    @Test fun announcedPerSlotCount() {
        assertEquals(AnnouncedMode(2560, 1600, 30), policy(1, StreamQuality.QHD).announced())
        assertEquals(AnnouncedMode(1920, 1200, 30), policy(1, StreamQuality.HD).announced())
        assertEquals(AnnouncedMode(1920, 1200, 30), policy(2, StreamQuality.UHD).announced())
        assertEquals(AnnouncedMode(1920, 1200, 30), policy(2, StreamQuality.HD).announced())
        assertEquals(AnnouncedMode(1280, 800, 30), policy(4, StreamQuality.QHD).announced())
        assertEquals(AnnouncedMode(1280, 800, 30), policy(3, StreamQuality.QHD).announced())
        assertEquals(AnnouncedMode(3456, 2160, 30), policy(1, StreamQuality.UHD).announced())
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

    @Test fun threeAt1920x1200Fit() {
        val p = policy(3)
        assertEquals(270_000, p.costOf(1920, 1200))
        for (i in 0..2) assertEquals(Admission.Accepted, p.admit(i, 1920, 1200))
    }

    @Test fun fourAt1280x800Fit() {
        val p = policy(4)
        assertEquals(120_000, p.costOf(1280, 800))
        for (i in 0..3) assertEquals(Admission.Accepted, p.admit(i, 1280, 800))
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

    @Test fun announcedSizesRespectBoxDecoderLimits() {
        val modes = StreamQuality.values().flatMap { q -> (1..4).map { policy(it, q).announced() } }.distinct()
        for (m in modes) {
            val blocks = ((m.width + 15) / 16) * ((m.height + 15) / 16)
            assertTrue("$m blocs", blocks <= BOX_MAX_BLOCKS)
            assertTrue("$m blocs/s", blocks * 30 <= BOX_MAX_BLOCKS_PER_SECOND)
        }
    }

    private companion object {
        // Limites de c2.amlogic.avc.decoder sur la box de test (media_codecs.xml).
        const val BOX_MAX_BLOCKS = 34_560
        const val BOX_MAX_BLOCKS_PER_SECOND = 1_036_800
    }

    @Test fun threeSourcesAnnounce1280x800AndFit() {
        val p = policy(3)
        assertEquals(AnnouncedMode(1280, 800, 30), p.announced())
        for (i in 0..2) assertEquals(Admission.Accepted, p.admit(i, 1280, 800))
    }
}
