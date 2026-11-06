package com.vantaflow.kairo.cast

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SpsWatcherTest {
    private fun b(vararg v: Int) = ByteArray(v.size) { v[it].toByte() }
    private val sps1 = b(0, 0, 0, 1, 0x67, 1, 2, 3)
    private val sps2 = b(0, 0, 0, 1, 0x67, 9, 9, 9)
    private val slice = b(0, 0, 0, 1, 0x41, 5, 5)

    @Test fun keyFrame() {
        assertTrue(H264.isKeyFrame(sps1, sps1.size))
        assertTrue(H264.isKeyFrame(b(0, 0, 1, 0x65, 1), 5))
        assertFalse(H264.isKeyFrame(slice, slice.size))
    }

    @Test fun extract() {
        val data = sps1 + b(0, 0, 0, 1, 0x68, 7)
        assertArrayEquals(b(0x67, 1, 2, 3), H264.extractSps(data, data.size))
        assertNull(H264.extractSps(slice, slice.size))
    }

    @Test fun changes() {
        val w = SpsWatcher()
        assertTrue(w.changed(sps1, sps1.size))
        assertFalse(w.changed(sps1, sps1.size))
        assertFalse(w.changed(slice, slice.size))
        assertTrue(w.changed(sps2, sps2.size))
    }
}
