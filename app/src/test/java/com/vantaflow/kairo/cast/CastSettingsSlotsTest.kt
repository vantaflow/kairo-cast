package com.vantaflow.kairo.cast

import java.util.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Test

class CastSettingsSlotsTest {
    private class FakeStore : KeyValueStore {
        val map = HashMap<String, String>()
        val writes = ArrayList<String>()
        override fun getString(k: String) = map[k]
        override fun putString(k: String, v: String) { map[k] = v; writes += k }
    }

    private fun settings(store: FakeStore = FakeStore()) = CastSettings(store, Random(7))

    @Test fun slot0KeysUnchanged() {
        val store = FakeStore()
        store.map["name"] = "Salon"
        store.map["device_id"] = "aa:bb:cc:dd:ee:02"
        store.map["display_mode"] = "FILL"
        val s = settings(store)
        assertEquals("Salon", s.slotName(0))
        assertEquals("aa:bb:cc:dd:ee:02", s.slotDeviceId(0))
        assertEquals(DisplayMode.FILL, s.slotDisplayMode(0))
        assertEquals(1, s.slotCount)
        assertFalse(store.map.keys.any { it.startsWith("slot0_") })
        assertFalse(store.writes.any { it.startsWith("slot0_") })
    }

    @Test fun migrationFromSingleSource() {
        val store = FakeStore()
        store.map["name"] = "Salon"
        store.map["device_id"] = "aa:bb:cc:dd:ee:02"
        val s = settings(store)
        assertEquals("Kairo 2", s.slotName(1))
        val id1 = s.slotDeviceId(1)
        assertNotEquals("aa:bb:cc:dd:ee:02", id1)
        assertEquals(id1, s.slotDeviceId(1))
        assertEquals(id1, store.map["slot1_device_id"])
        assertEquals(id1, settings(store).slotDeviceId(1))
    }

    @Test fun roundTripKeepsIdentities() {
        val s = settings()
        s.slotCount = 4
        val ids = (0..3).map { s.slotDeviceId(it) }
        val names = (0..3).map { s.slotName(it) }
        s.slotCount = 2
        s.slotCount = 4
        assertEquals(ids, (0..3).map { s.slotDeviceId(it) })
        assertEquals(names, (0..3).map { s.slotName(it) })
        assertEquals(4, ids.toSet().size)
    }

    @Test fun duplicateRejectedCaseInsensitive() {
        val s = settings()
        s.setSlotName(0, "Salon")
        assertEquals(NameResult.Duplicate, s.setSlotName(1, "salon"))
        assertEquals("Kairo 2", s.slotName(1))
        assertEquals(NameResult.Duplicate, s.setSlotName(1, "  SALON "))
        assertEquals(NameResult.Ok, s.setSlotName(1, "Bureau"))
        assertEquals("Bureau", s.slotName(1))
    }

    @Test fun hiddenSlotNameDoesNotBlock() {
        val store = FakeStore()
        store.map["slot_count"] = "2"
        store.map["slot3_name"] = "Cuisine"
        assertEquals(NameResult.Ok, settings(store).setSlotName(1, "cuisine"))
    }

    @Test fun emptyNameFallsBackToDefaultOfSlot() {
        val s = settings()
        assertEquals(NameResult.Ok, s.setSlotName(2, ""))
        assertEquals("Kairo 3", s.slotName(2))
        s.setSlotName(2, "Zed")
        s.setSlotName(0, "Kairo 3")
        assertEquals(NameResult.Duplicate, s.setSlotName(2, ""))
    }

    @Test fun legacyNameSetterRefusesDuplicate() {
        val s = settings()
        s.slotCount = 2
        s.setSlotName(1, "Bureau")
        s.name = "bureau"
        assertEquals("Kairo", s.name)
        s.name = "Salon"
        assertEquals("Salon", s.name)
    }

    @Test fun invalidSlotCountFallsBack() {
        val store = FakeStore()
        store.map["slot_count"] = "3"
        assertEquals(1, settings(store).slotCount)
        store.map["slot_count"] = "7"
        assertEquals(1, settings(store).slotCount)
    }

    @Test fun defaultModeFitWhenMultiple() {
        val store = FakeStore()
        val s = settings(store)
        assertEquals(DisplayMode.STRETCH, s.slotDisplayMode(0))
        s.slotCount = 2
        assertEquals(DisplayMode.FIT, s.slotDisplayMode(0))
        assertEquals(DisplayMode.FIT, s.slotDisplayMode(1))
        store.map["display_mode"] = "STRETCH"
        assertEquals(DisplayMode.STRETCH, s.slotDisplayMode(0))
        s.setSlotDisplayMode(1, DisplayMode.FILL)
        assertEquals(DisplayMode.FILL, s.slotDisplayMode(1))
    }

    @Test fun slotNamesListsActiveOnly() {
        val s = settings()
        s.slotCount = 2
        assertEquals(listOf("Kairo", "Kairo 2"), s.slotNames)
    }
}
