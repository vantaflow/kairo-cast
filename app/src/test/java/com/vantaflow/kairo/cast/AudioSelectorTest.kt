package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class AudioSelectorTest {
    private var now = 0L
    private val calls = ArrayList<Int?>()
    private fun selector() = AudioSelector(1000) { now }.also { it.listener = { s -> calls += s } }

    @Test fun firstActiveGetsAudio() {
        val s = selector()
        s.onActiveChanged(listOf(1))
        assertEquals(1, s.audioSlot)
    }

    @Test fun arrivalDoesNotStealAudio() {
        val s = selector()
        s.onActiveChanged(listOf(1))
        s.onActiveChanged(listOf(0, 1))
        assertEquals(1, s.audioSlot)
    }

    @Test fun departureMovesToSmallestRemaining() {
        val s = selector()
        s.onActiveChanged(listOf(0))
        s.onActiveChanged(listOf(0, 1, 2))
        s.onActiveChanged(listOf(2, 1))
        assertEquals(1, s.audioSlot)
    }

    @Test fun lastDepartureClearsAudio() {
        val s = selector()
        s.onActiveChanged(listOf(1))
        s.onActiveChanged(emptyList())
        assertNull(s.audioSlot)
    }

    @Test fun arrivalWhenNobodyHadAudioTakesIt() {
        val s = selector()
        s.onActiveChanged(emptyList())
        s.onActiveChanged(listOf(2, 3))
        assertEquals(2, s.audioSlot)
    }

    @Test fun focusChangesAudioAfterDelay() {
        val s = selector()
        s.onActiveChanged(listOf(0, 2))
        now = 0; s.onFocus(2)
        now = 500; s.tick()
        assertEquals(0, s.audioSlot)
        now = 1000; s.tick()
        assertEquals(2, s.audioSlot)
    }

    @Test fun fastFocusSweepDoesNotFlap() {
        val s = selector()
        s.onActiveChanged(listOf(0, 1, 2, 3))
        calls.clear()
        now = 0; s.onFocus(1); s.tick()
        now = 400; s.onFocus(2); s.tick()
        now = 800; s.onFocus(3); s.tick()
        now = 1700; s.tick()
        assertEquals(0, s.audioSlot)
        now = 1800; s.tick()
        assertEquals(3, s.audioSlot)
        assertEquals(listOf<Int?>(3), calls)
    }

    @Test fun focusOnInactiveIgnored() {
        val s = selector()
        s.onActiveChanged(listOf(0, 1))
        now = 0; s.onFocus(3)
        now = 5000; s.tick()
        assertEquals(0, s.audioSlot)
    }

    @Test fun listenerCalledOnlyOnChange() {
        val s = selector()
        s.onActiveChanged(listOf(0))
        s.onActiveChanged(listOf(0, 1))
        now = 0; s.onFocus(0)
        now = 2000; s.tick(); s.tick()
        assertEquals(listOf<Int?>(0), calls)
    }
}
