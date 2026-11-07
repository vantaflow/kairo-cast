package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class MirrorBusPerSlotTest {
    private val released = ArrayList<String>()
    private val reg = SlotRegistry<String>({ released += it }) { "p$it" }

    @Test fun stopSlotLeavesOthers() {
        reg.start(0); reg.start(1)
        reg.stop(0)
        assertNull(reg.get(0))
        assertNotNull(reg.get(1))
        assertEquals(listOf("p0"), released)
        assertEquals(1, reg.firstSlot())
    }

    @Test fun restartReleasesPrevious() {
        reg.start(2)
        reg.start(2)
        assertEquals(listOf("p2"), released)
    }

    @Test fun stopAllReleasesEverything() {
        reg.start(0); reg.start(3)
        reg.stopAll()
        assertEquals(setOf("p0", "p3"), released.toSet())
        assertTrue(reg.firstSlot() == null)
    }
}
