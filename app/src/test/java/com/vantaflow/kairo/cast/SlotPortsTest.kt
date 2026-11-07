package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class SlotPortsTest {
    @Test fun slot0IsHistorical() {
        assertEquals(SlotPorts(7000, 7100, 7011, 6001, 6000, 7300), SlotPorts.of(0))
    }

    @Test fun slotsAreStrictlyDisjoint() {
        val all = (0 until 4).flatMap { SlotPorts.of(it).all }
        assertEquals(24, all.size)
        assertEquals(24, all.toSet().size)
        for (i in 0 until 4) assertEquals(6, SlotPorts.of(i).all.toSet().size)
    }

    @Test fun offsetsFollowRule() {
        for (i in 0 until MAX_SLOTS) {
            val p = SlotPorts.of(i)
            assertEquals(7000 + 10 * i, p.airplayHttp)
            assertEquals(7100 + 10 * i, p.airplayMirror)
            assertEquals(7011 + 10 * i, p.udpTiming)
            assertEquals(6001 + 10 * i, p.udpControl)
            assertEquals(6000 + 10 * i, p.udpData)
            assertEquals(7300 + i, p.link)
        }
    }

    @Test fun outOfRangeThrows() {
        assertThrows(IllegalArgumentException::class.java) { SlotPorts.of(-1) }
        assertThrows(IllegalArgumentException::class.java) { SlotPorts.of(4) }
    }

    @Test fun noPortInUdpTcpOverlapWithOtherSlotRanges() {
        assertEquals(SlotPorts(7030, 7130, 7041, 6031, 6030, 7303), SlotPorts.of(3))
    }
}
