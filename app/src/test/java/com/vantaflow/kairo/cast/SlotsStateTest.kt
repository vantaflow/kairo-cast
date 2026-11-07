package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SlotsStateTest {
    private fun st(slot: Int, p: SlotPhase, w: Int = 0, h: Int = 0) = SlotState(slot, "S$slot", p, w, h)

    @Test fun activeListIsSortedAndExcludesWaiting() {
        SlotsState.reset(4, listOf("A", "B", "C", "D"))
        SlotsState.update(st(2, SlotPhase.MIRRORING, 1280, 800))
        SlotsState.update(st(0, SlotPhase.SATURATED))
        SlotsState.update(st(1, SlotPhase.WAITING))
        SlotsState.update(st(3, SlotPhase.MAC_WAITING))
        assertEquals(listOf(0, 2), SlotsState.active.value)
    }

    @Test fun errorSlotDoesNotStopOthers() {
        SlotsState.reset(2, listOf("A", "B"))
        SlotsState.update(st(1, SlotPhase.ERROR))
        SlotsState.update(st(0, SlotPhase.MIRRORING, 1920, 1200))
        assertEquals(listOf(0), SlotsState.active.value)
        assertEquals(Status.Mirroring(1920, 1200), ReceiverState.status.value)
    }

    @Test fun noneActiveCallbackOnlyWhenLastLeaves() {
        assertFalse(SlotsState.shouldClose(listOf(0, 1), listOf(1)))
        assertTrue(SlotsState.shouldClose(listOf(1), emptyList()))
        assertFalse(SlotsState.shouldClose(emptyList(), emptyList()))
    }

    @Test fun statusDerivation() {
        assertEquals(Status.Stopped, SlotsState.statusOf(emptyList()))
        assertEquals(Status.Stopped, SlotsState.statusOf(listOf(st(0, SlotPhase.STOPPED), st(1, SlotPhase.ERROR))))
        assertEquals(Status.Waiting, SlotsState.statusOf(listOf(st(0, SlotPhase.WAITING), st(1, SlotPhase.ERROR))))
        assertEquals(Status.MacWaiting, SlotsState.statusOf(listOf(st(0, SlotPhase.MAC_WAITING))))
        assertEquals(Status.Waiting, SlotsState.statusOf(listOf(st(0, SlotPhase.WAITING), st(1, SlotPhase.MAC_WAITING))))
        assertEquals(
            Status.Mirroring(10, 20),
            SlotsState.statusOf(listOf(st(0, SlotPhase.MAC_WAITING), st(1, SlotPhase.MIRRORING, 10, 20))),
        )
    }

    @Test fun removeDropsSlot() {
        SlotsState.reset(2, listOf("A", "B"))
        SlotsState.remove(1)
        assertEquals(listOf(0), SlotsState.slots.value.map { it.slot })
    }
}
