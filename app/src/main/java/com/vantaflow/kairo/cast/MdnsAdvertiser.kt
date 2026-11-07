package com.vantaflow.kairo.cast

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Surface NSD utilisée par l'annonceur (remplaçable en test JVM). */
interface NsdApi {
    fun register(info: NsdServiceInfo, listener: NsdManager.RegistrationListener)
    fun unregister(listener: NsdManager.RegistrationListener)
}

class MdnsAdvertiser(private val nsd: NsdApi, private val locks: MulticastLockHolder) {
    constructor(ctx: Context, locks: MulticastLockHolder = MulticastLockHolder.of(ctx)) : this(
        object : NsdApi {
            private val m = ctx.applicationContext.getSystemService(Context.NSD_SERVICE) as NsdManager
            override fun register(info: NsdServiceInfo, listener: NsdManager.RegistrationListener) =
                m.registerService(info, NsdManager.PROTOCOL_DNS_SD, listener)
            override fun unregister(listener: NsdManager.RegistrationListener) = m.unregisterService(listener)
        },
        locks,
    )

    private var locked = false
    private val listeners = ArrayList<NsdManager.RegistrationListener>()
    private val pending = ArrayList<CountDownLatch>()
    private val registered = ArrayList<CountDownLatch>()

    @Synchronized
    fun register(
        name: String,
        raopName: String,
        port: Int,
        airplayTxt: Map<String, String>,
        raopTxt: Map<String, String>,
        linkPort: Int = 0,
        slot: Int = 0,
    ) {
        unregister()
        locks.acquire()
        locked = true
        try {
            add("_airplay._tcp", name, port, airplayTxt)
            add("_raop._tcp", raopName, port, raopTxt)
            // nom d'instance distinct : NsdManager refuse deux services de même nom (conflit)
            if (linkPort > 0) add("_kairocast._tcp", "$name Link", linkPort, mapOf("name" to name, "v" to "1", "slot" to slot.toString()))
        } catch (e: Exception) {
            unregister() // pas de service fantôme ni de verrou multicast retenu
            throw e
        }
    }

    private fun add(type: String, name: String, port: Int, txt: Map<String, String>) {
        val info = NsdServiceInfo().apply {
            serviceName = name
            serviceType = type
            this.port = port
            txt.forEach { (k, v) -> setAttribute(k, v) }
        }
        val done = CountDownLatch(1)
        val reg = CountDownLatch(1)
        pending.add(done)
        registered.add(reg)
        val l = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(i: NsdServiceInfo) {
                Log.i(TAG, "annonce $type enregistrée : ${i.serviceName}")
                reg.countDown()
            }
            override fun onRegistrationFailed(i: NsdServiceInfo, code: Int) {
                Log.e(TAG, "annonce $type refusée ($code)")
                reg.countDown()
                done.countDown()
            }
            override fun onServiceUnregistered(i: NsdServiceInfo) {
                Log.i(TAG, "annonce $type retirée")
                done.countDown()
            }
            override fun onUnregistrationFailed(i: NsdServiceInfo, code: Int) {
                Log.w(TAG, "retrait $type échoué ($code)")
                done.countDown()
            }
        }
        listeners.add(l)
        nsd.register(info, l)
    }

    /** Bloquant (jusqu'à ~3 s) : ne pas appeler depuis le thread principal. */
    @Synchronized
    fun unregister() {
        try {
            // Retirer avant la fin de l'enregistrement laisserait un service fantôme : on attend d'abord.
            val regDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3)
            registered.forEach {
                val left = regDeadline - System.nanoTime()
                if (left > 0) it.await(left, TimeUnit.NANOSECONDS)
            }
            listeners.forEach {
                try { nsd.unregister(it) } catch (e: Exception) { Log.w(TAG, "retrait ignoré : $e") }
            }
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3)
            pending.forEach {
                val left = deadline - System.nanoTime()
                if (left > 0) it.await(left, TimeUnit.NANOSECONDS)
            }
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        } finally {
            listeners.clear()
            pending.clear()
            registered.clear()
            if (locked) { locked = false; locks.release() }
        }
    }

    private companion object {
        const val TAG = "KairoCast"
    }
}
