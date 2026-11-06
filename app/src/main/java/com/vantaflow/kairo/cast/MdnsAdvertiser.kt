package com.vantaflow.kairo.cast

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.util.Log

class MdnsAdvertiser(ctx: Context) {
    private val app = ctx.applicationContext
    private val nsd = app.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var lock: WifiManager.MulticastLock? = null
    private val listeners = ArrayList<NsdManager.RegistrationListener>()

    @Synchronized
    fun register(
        name: String,
        raopName: String,
        port: Int,
        airplayTxt: Map<String, String>,
        raopTxt: Map<String, String>,
    ) {
        unregister()
        val wifi = app.getSystemService(Context.WIFI_SERVICE) as WifiManager
        lock = wifi.createMulticastLock("kairocast-mdns").apply {
            setReferenceCounted(false)
            acquire()
        }
        add("_airplay._tcp", name, port, airplayTxt)
        add("_raop._tcp", raopName, port, raopTxt)
    }

    private fun add(type: String, name: String, port: Int, txt: Map<String, String>) {
        val info = NsdServiceInfo().apply {
            serviceName = name
            serviceType = type
            this.port = port
            txt.forEach { (k, v) -> setAttribute(k, v) }
        }
        val l = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(i: NsdServiceInfo) {
                Log.i(TAG, "annonce $type enregistrée : ${i.serviceName}")
            }
            override fun onRegistrationFailed(i: NsdServiceInfo, code: Int) {
                Log.e(TAG, "annonce $type refusée ($code)")
            }
            override fun onServiceUnregistered(i: NsdServiceInfo) {
                Log.i(TAG, "annonce $type retirée")
            }
            override fun onUnregistrationFailed(i: NsdServiceInfo, code: Int) {
                Log.w(TAG, "retrait $type échoué ($code)")
            }
        }
        listeners.add(l)
        nsd.registerService(info, NsdManager.PROTOCOL_DNS_SD, l)
    }

    @Synchronized
    fun unregister() {
        listeners.forEach {
            try { nsd.unregisterService(it) } catch (e: IllegalArgumentException) { /* jamais enregistré */ }
        }
        listeners.clear()
        lock?.let { if (it.isHeld) it.release() }
        lock = null
    }

    private companion object {
        const val TAG = "KairoCast"
    }
}
