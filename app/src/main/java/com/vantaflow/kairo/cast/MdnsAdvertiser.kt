package com.vantaflow.kairo.cast

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.util.Log
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class MdnsAdvertiser(ctx: Context) {
    private val app = ctx.applicationContext
    private val nsd = app.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var lock: WifiManager.MulticastLock? = null
    private val listeners = ArrayList<NsdManager.RegistrationListener>()
    private val pending = ArrayList<CountDownLatch>()

    @Synchronized
    fun register(
        name: String,
        raopName: String,
        port: Int,
        airplayTxt: Map<String, String>,
        raopTxt: Map<String, String>,
        linkPort: Int = 0,
    ) {
        unregister()
        val wifi = app.getSystemService(Context.WIFI_SERVICE) as WifiManager
        lock = wifi.createMulticastLock("kairocast-mdns").apply {
            setReferenceCounted(false)
            acquire()
        }
        add("_airplay._tcp", name, port, airplayTxt)
        add("_raop._tcp", raopName, port, raopTxt)
        if (linkPort > 0) add(LinkProtocol.SERVICE_TYPE, name, linkPort, mapOf("v" to LinkProtocol.VERSION.toString()))
    }

    private fun add(type: String, name: String, port: Int, txt: Map<String, String>) {
        val info = NsdServiceInfo().apply {
            serviceName = name
            serviceType = type
            this.port = port
            txt.forEach { (k, v) -> setAttribute(k, v) }
        }
        val done = CountDownLatch(1)
        pending.add(done)
        val l = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(i: NsdServiceInfo) {
                Log.i(TAG, "annonce $type enregistrée : ${i.serviceName}")
            }
            override fun onRegistrationFailed(i: NsdServiceInfo, code: Int) {
                Log.e(TAG, "annonce $type refusée ($code)")
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
        nsd.registerService(info, NsdManager.PROTOCOL_DNS_SD, l)
    }

    /** Bloquant (jusqu'à ~3 s) : ne pas appeler depuis le thread principal. */
    @Synchronized
    fun unregister() {
        try {
            listeners.forEach {
                try { nsd.unregisterService(it) } catch (e: Exception) { Log.w(TAG, "retrait ignoré : $e") }
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
            try { lock?.let { if (it.isHeld) it.release() } } catch (e: Exception) { }
            lock = null
        }
    }

    private companion object {
        const val TAG = "KairoCast"
    }
}
