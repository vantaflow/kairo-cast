package com.vantaflow.kairo.cast.sender

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SendQueueTest {
    private fun b(n: Int, v: Int = 0) = ByteArray(n) { v.toByte() }

    @Test fun fifoOrder() {
        val q = SendQueue(100)
        q.offerVideo(b(10, 1), true)
        q.offer(b(5, 2))
        q.offerVideo(b(10, 3), false)
        assertEquals(1.toByte(), q.take()!![0])
        assertEquals(2.toByte(), q.take()!![0])
        assertEquals(3.toByte(), q.take()!![0])
    }

    @Test fun overflowDropsUntilNextKeyFrame() {
        val q = SendQueue(100)
        assertEquals(SendQueue.Result.QUEUED, q.offerVideo(b(60), true))
        assertEquals(SendQueue.Result.NEED_KEY, q.offerVideo(b(60), false))
        q.take() // la file se vide, mais on attend toujours une image clé
        assertEquals(SendQueue.Result.DROPPED, q.offerVideo(b(10), false))
        assertEquals(SendQueue.Result.QUEUED, q.offerVideo(b(10), true))
        assertEquals(SendQueue.Result.QUEUED, q.offerVideo(b(10), false))
    }

    @Test fun keyFrameReplacesQueuedVideoButKeepsAudio() {
        val q = SendQueue(100)
        q.offerVideo(b(40, 1), false)
        q.offer(b(10, 2))
        q.offerVideo(b(40, 3), false)
        q.offerVideo(b(60, 4), true)
        assertEquals(2.toByte(), q.take()!![0])
        assertEquals(4.toByte(), q.take()!![0])
        assertEquals(0L, q.queuedBytes)
    }

    @Test fun audioDroppedWhenFull() {
        val q = SendQueue(20)
        assertTrue(q.offer(b(15)))
        assertEquals(false, q.offer(b(15)))
    }

    @Test fun closeWakesTaker() {
        val q = SendQueue(100)
        var got: ByteArray? = b(1)
        val t = Thread { got = q.take() }.apply { start() }
        Thread.sleep(50)
        q.close()
        t.join(1000)
        assertNull(got)
    }
}
