package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Test

class SlotSupervisorTest {
    private val log = ArrayList<String>()
    private val failing = HashSet<Int>()

    private fun sup() = SlotSupervisor { slot ->
        object : SlotRunner {
            override fun start(): Boolean { log += "start$slot"; return slot !in failing }
            override fun stop() { log += "stop$slot" }
        }
    }

    @Test fun startsNInOrder() {
        val s = sup()
        s.apply(listOf("a", "b", "c"))
        assertEquals(listOf("start0", "start1", "start2"), log)
        assertEquals(listOf(0, 1, 2), s.slots)
    }

    @Test fun sameKeysDoNothing() {
        val s = sup()
        s.apply(listOf("a", "b")); log.clear()
        s.apply(listOf("a", "b"))
        assertEquals(emptyList<String>(), log)
    }

    @Test fun countChangeOneToTwoToOne() {
        val s = sup()
        s.apply(listOf("a:1"))
        log.clear()
        // le nombre d'emplacements fait partie de la clé : tous redémarrent
        s.apply(listOf("a:2", "b:2"))
        assertEquals(listOf("stop0", "start0", "start1"), log)
        log.clear()
        s.apply(listOf("a:1"))
        assertEquals(listOf("stop1", "stop0", "start0").sorted(), log.sorted())
        assertEquals(listOf(0), s.slots)
    }

    @Test fun renameRestartsOnlyThatSlot() {
        val s = sup()
        s.apply(listOf("a", "b", "c")); log.clear()
        s.apply(listOf("a", "B", "c"))
        assertEquals(listOf("stop1", "start1"), log)
    }

    @Test fun failedSlotDoesNotStopOthersAndIsRetried() {
        failing += 1
        val s = sup()
        s.apply(listOf("a", "b", "c"))
        assertEquals(listOf("start0", "start1", "start2"), log)
        log.clear(); failing.clear()
        s.apply(listOf("a", "b", "c"))
        assertEquals(listOf("stop1", "start1"), log)
    }

    @Test fun stopAllStopsEverything() {
        val s = sup()
        s.apply(listOf("a", "b")); log.clear()
        s.stopAll()
        assertEquals(setOf("stop0", "stop1"), log.toSet())
        assertEquals(emptyList<Int>(), s.slots)
    }
}
