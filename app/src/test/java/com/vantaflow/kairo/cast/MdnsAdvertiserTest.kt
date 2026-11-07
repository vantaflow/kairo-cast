package com.vantaflow.kairo.cast

import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class MdnsAdvertiserTest {
    private class Lock : LockHandle {
        var held = 0
        override fun acquire() { held++ }
        override fun release() { held-- }
    }

    private class FakeNsd(val failOn: Int) : NsdApi {
        var registers = 0
        val live = ArrayList<NsdManager.RegistrationListener>()
        override fun register(info: NsdServiceInfo, listener: NsdManager.RegistrationListener) {
            if (++registers == failOn) throw IllegalArgumentException("refus")
            live += listener
            listener.onServiceRegistered(info)
        }
        override fun unregister(listener: NsdManager.RegistrationListener) {
            live.remove(listener)
            listener.onServiceUnregistered(NsdServiceInfo())
        }
    }

    @Test fun registerFailureUnregistersAndReleasesLock() {
        val lock = Lock()
        val nsd = FakeNsd(failOn = 2)
        val adv = MdnsAdvertiser(nsd, MulticastLockHolder(lock))
        try {
            adv.register("Kairo", "AA@Kairo", 7000, emptyMap(), emptyMap(), 7300, 0)
            fail("attendu")
        } catch (e: IllegalArgumentException) {}
        assertTrue(nsd.live.isEmpty())
        assertEquals(0, lock.held)
    }

    @Test fun registerThenUnregisterBalancesLock() {
        val lock = Lock()
        val nsd = FakeNsd(failOn = -1)
        val adv = MdnsAdvertiser(nsd, MulticastLockHolder(lock))
        adv.register("Kairo", "AA@Kairo", 7000, emptyMap(), emptyMap(), 7300, 0)
        assertEquals(3, nsd.live.size)
        assertEquals(1, lock.held)
        adv.unregister()
        assertTrue(nsd.live.isEmpty())
        assertEquals(0, lock.held)
    }
}
