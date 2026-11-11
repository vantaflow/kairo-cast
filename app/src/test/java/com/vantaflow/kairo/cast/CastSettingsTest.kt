package com.vantaflow.kairo.cast

import java.util.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CastSettingsTest {
    private class MapStore : KeyValueStore {
        val map = HashMap<String, String>()
        override fun getString(k: String) = map[k]
        override fun putString(k: String, v: String) { map[k] = v }
    }

    @Test fun defaults() {
        val s = CastSettings(MapStore(), Random(1))
        assertEquals("Kairo", s.name)
        assertEquals(DisplayMode.STRETCH, s.displayMode)
        assertTrue(s.audioEnabled)
        assertTrue(s.startOnBoot)
        assertFalse(s.openOnPowerOn)
        assertTrue(Regex("^([0-9a-f]{2}:){5}[0-9a-f]{2}$").matches(s.deviceId))
    }

    @Test fun qualityDefaultsToQhd() {
        assertEquals(StreamQuality.QHD, CastSettings(MapStore(), Random(1)).quality)
    }

    @Test fun qualityRoundTrip() {
        val store = MapStore()
        CastSettings(store, Random(1)).quality = StreamQuality.UHD
        assertEquals(StreamQuality.UHD, CastSettings(store, Random(1)).quality)
    }

    @Test fun unknownQualityFallsBack() {
        val store = MapStore()
        store.putString("stream_quality", "WAT")
        assertEquals(StreamQuality.QHD, CastSettings(store, Random(1)).quality)
    }

    @Test fun deviceIdStable() {
        val store = MapStore()
        val a = CastSettings(store, Random(1)).deviceId
        assertEquals(a, CastSettings(store, Random(2)).deviceId)
    }

    @Test fun unknownDisplayModeFallsBack() {
        val store = MapStore()
        store.putString("display_mode", "WAT")
        assertEquals(DisplayMode.STRETCH, CastSettings(store, Random(1)).displayMode)
    }

    @Test fun roundTrip() {
        val store = MapStore()
        val s = CastSettings(store, Random(1))
        s.displayMode = DisplayMode.FIT
        s.audioEnabled = false
        s.startOnBoot = false
        s.openOnPowerOn = true
        s.name = "Salon"
        val t = CastSettings(store, Random(1))
        assertEquals(DisplayMode.FIT, t.displayMode)
        assertFalse(t.audioEnabled)
        assertFalse(t.startOnBoot)
        assertTrue(t.openOnPowerOn)
        assertEquals("Salon", t.name)
    }

    @Test fun emptyNameBecomesDefault() {
        val s = CastSettings(MapStore(), Random(1))
        s.name = ""
        assertEquals("Kairo", s.name)
    }
}
