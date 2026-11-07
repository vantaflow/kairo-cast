package com.vantaflow.kairo.cast

import org.junit.Assert.*
import org.junit.Test
import java.util.Random

class SettingsRowsTest {
    private val labels = object : SettingsRows.Labels {
        override fun sourceN(n: Int, name: String) = "Source $n : $name"
        override fun qualityFixed() = "fixé"
        override fun quality(q: StreamQuality) = "Q-" + q.name
    }
    private class MemStore : KeyValueStore {
        val m = HashMap<String, String>()
        override fun getString(k: String) = m[k]
        override fun putString(k: String, v: String) { m[k] = v }
    }
    private fun settings(count: Int = 1, vararg names: String): CastSettings {
        val s = CastSettings(MemStore(), Random(1))
        s.slotCount = count
        names.forEachIndexed { i, n -> s.setSlotName(i, n) }
        return s
    }
    private fun snap(count: Int, names: List<String>, q: StreamQuality = StreamQuality.QHD) =
        SettingsRows.Snapshot(count, names, q)

    @Test fun singleSourceKeepsOneNameRow() {
        val rows = SettingsRows.nameRows(settings(1), labels)
        assertEquals(listOf("name"), rows.map { it.id })
        assertEquals("Kairo", rows[0].value)
    }
    @Test fun fourSourcesFourRows() {
        val rows = SettingsRows.nameRows(settings(4), labels)
        assertEquals(listOf("name_0", "name_1", "name_2", "name_3"), rows.map { it.id })
        assertEquals("Source 1 : Kairo", rows[0].value)
        assertEquals("Source 2 : Kairo 2", rows[1].value)
    }
    @Test fun qualityDisabledFromTwoSources() {
        assertTrue(SettingsRows.qualityRow(settings(1), labels).enabled)
        val r = SettingsRows.qualityRow(settings(2), labels)
        assertFalse(r.enabled)
        assertEquals("fixé", r.value)
    }
    @Test fun slotCountCycle() {
        assertEquals(2, SettingsRows.nextSlotCount(1))
        assertEquals(4, SettingsRows.nextSlotCount(2))
        assertEquals(1, SettingsRows.nextSlotCount(4))
    }
    @Test fun restartWhenCountNameOrQualityChanges() {
        val base = snap(2, listOf("A", "B", "C", "D"))
        assertTrue(SettingsRows.needsRestart(base, snap(4, listOf("A", "B", "C", "D"))))
        assertTrue(SettingsRows.needsRestart(base, snap(2, listOf("A", "X", "C", "D"))))
        assertTrue(SettingsRows.needsRestart(base, snap(2, listOf("A", "B", "C", "D"), StreamQuality.UHD)))
        assertFalse(SettingsRows.needsRestart(base, base.copy()))
    }
    @Test fun restartIgnoresHiddenSlotNames() {
        assertFalse(SettingsRows.needsRestart(snap(2, listOf("A", "B", "C", "D")), snap(2, listOf("A", "B", "Z", "D"))))
    }
}
