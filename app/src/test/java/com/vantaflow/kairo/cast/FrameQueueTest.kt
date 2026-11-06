package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class FrameQueueTest {
    private fun f(pts: Long, key: Boolean) = Frame(byteArrayOf(1), 1, pts, key)

    @Test fun overflowDropsUntilKeyFrame() {
        val q = FrameQueue(3)
        q.offer(f(0, true))
        q.offer(f(1, false))
        q.offer(f(2, false))
        q.offer(f(3, false)) // pleine : vidée
        assertNull(q.poll(10))
        q.offer(f(4, false)) // ignorée
        assertNull(q.poll(10))
        q.offer(f(5, true))
        assertEquals(5L, q.poll(10)?.ptsUs)
    }

    @Test fun fifoOrder() {
        val q = FrameQueue(3)
        q.offer(f(0, true)); q.offer(f(1, false))
        assertEquals(0L, q.poll(10)?.ptsUs)
        assertEquals(1L, q.poll(10)?.ptsUs)
    }

    @Test fun pollBlocksUntilOffer() {
        val q = FrameQueue(3)
        Thread { Thread.sleep(50); q.offer(f(7, true)) }.start()
        assertNotNull(q.poll(2000))
    }

    @Test fun clearEmpties() {
        val q = FrameQueue(3)
        q.offer(f(0, true))
        q.clear()
        assertNull(q.poll(10))
    }
}
