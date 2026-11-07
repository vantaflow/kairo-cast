package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class FrameQueueTest {
    private fun f(pts: Long, key: Boolean) = Frame(byteArrayOf(1), 1, pts, key)

    @Test fun overflowDropsOldestNonKeyAndKeepsAccepting() {
        val q = FrameQueue(3, 1)
        q.offer(f(0, true))
        q.offer(f(1, false))
        q.offer(f(2, false))
        q.offer(f(3, false)) // pleine : retire la plus ancienne non clé (1)
        q.offer(f(4, false))
        assertEquals(0L, q.poll(10)?.ptsUs)
        assertEquals(3L, q.poll(10)?.ptsUs)
        assertEquals(4L, q.poll(10)?.ptsUs)
        q.offer(f(5, false)) // toujours acceptée
        assertEquals(5L, q.poll(10)?.ptsUs)
    }

    @Test fun overflowAllKeyDropsOldest() {
        val q = FrameQueue(2, 1)
        q.offer(f(0, true)); q.offer(f(1, true)); q.offer(f(2, true))
        assertEquals(1L, q.poll(10)?.ptsUs)
        assertEquals(2L, q.poll(10)?.ptsUs)
    }

    @Test fun producerWaitsForConsumer() {
        val q = FrameQueue(1, 2000)
        q.offer(f(0, false))
        Thread { Thread.sleep(50); q.poll(10) }.start()
        q.offer(f(1, false)) // attend la place libérée, rien n'est perdu
        assertEquals(1L, q.poll(10)?.ptsUs)
        assertEquals(0L, q.dropped)
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
