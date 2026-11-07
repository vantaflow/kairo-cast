package com.vantaflow.kairo.cast

import android.content.Context
import android.net.wifi.WifiManager

interface LockHandle {
    fun acquire()
    fun release()
}

/** Verrou multicast partagé, compté par références : pris au premier acquire, relâché au dernier release. */
class MulticastLockHolder(private val handle: LockHandle) {
    private var refs = 0

    @Synchronized
    fun acquire() {
        if (refs++ == 0) handle.acquire()
    }

    @Synchronized
    fun release() {
        if (refs == 0) return
        if (--refs == 0) handle.release()
    }

    companion object {
        @Volatile private var instance: MulticastLockHolder? = null

        fun of(ctx: Context): MulticastLockHolder = instance ?: synchronized(this) {
            instance ?: run {
                val wifi = ctx.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                val lock = wifi.createMulticastLock("kairocast-mdns").apply { setReferenceCounted(false) }
                MulticastLockHolder(object : LockHandle {
                    override fun acquire() { lock.acquire() }
                    override fun release() { try { if (lock.isHeld) lock.release() } catch (_: Exception) {} }
                })
            }.also { instance = it }
        }
    }
}
