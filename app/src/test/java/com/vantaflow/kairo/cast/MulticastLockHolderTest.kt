package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Test

class MulticastLockHolderTest {
    private class Fake : LockHandle {
        var acquires = 0
        var releases = 0
        override fun acquire() { acquires++ }
        override fun release() { releases++ }
    }

    @Test fun lockTakenOnceReleasedOnLast() {
        val f = Fake()
        val h = MulticastLockHolder(f)
        h.acquire(); h.acquire(); h.acquire()
        assertEquals(1, f.acquires)
        h.release(); h.release()
        assertEquals(0, f.releases)
        h.release()
        assertEquals(1, f.releases)
    }

    @Test fun releaseWithoutAcquireIsSafe() {
        val f = Fake()
        val h = MulticastLockHolder(f)
        h.release()
        assertEquals(0, f.releases)
        h.acquire()
        assertEquals(1, f.acquires)
    }
}
