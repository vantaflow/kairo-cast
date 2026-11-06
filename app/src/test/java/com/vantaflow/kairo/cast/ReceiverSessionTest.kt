package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ReceiverSessionTest {
    @Test fun idempotent() {
        var starts = 0; var stops = 0
        val s = ReceiverSession({ starts++ }, { stops++ })
        s.opened(); s.opened()
        assertTrue(s.active)
        s.closed()
        assertFalse(s.active)
        s.opened()
        assertEquals(2, starts)
        assertEquals(1, stops)
    }

    @Test fun closedWithoutOpened() {
        var stops = 0
        ReceiverSession({}, { stops++ }).closed()
        assertEquals(0, stops)
    }
}
