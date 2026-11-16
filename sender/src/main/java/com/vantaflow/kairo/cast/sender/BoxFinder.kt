package com.vantaflow.kairo.cast.sender

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import android.util.Log

/** Une source de la box, annoncée en mDNS (_kairocast._tcp). */
data class Box(val id: String, val name: String, val host: String, val port: Int)

/** Nom affiché : attribut TXT « name », sinon le nom du service sans le suffixe « Link ». */
fun boxDisplayName(serviceName: String, txtName: String?): String =
    txtName?.trim()?.takeIf { it.isNotEmpty() } ?: serviceName.removeSuffix(" Link").trim()

/**
 * Cherche les sources Kairo Link du réseau. [onChange] reçoit la liste triée, sur le fil principal.
 * Les résolutions passent une par une (Android refuse deux résolutions simultanées avant la version 14).
 */
class BoxFinder(context: Context, private val onChange: (List<Box>) -> Unit) {
    private val nsd = context.getSystemService(NsdManager::class.java)
    private val wifi = context.applicationContext.getSystemService(WifiManager::class.java)
    private val main = Handler(Looper.getMainLooper())
    private val boxes = linkedMapOf<String, Box>()
    private val toResolve = ArrayDeque<NsdServiceInfo>()
    private var resolving = false
    private var lock: WifiManager.MulticastLock? = null
    private var listener: NsdManager.DiscoveryListener? = null

    fun start() {
        if (listener != null) return
        lock = wifi?.createMulticastLock("kairo-envoi")?.apply { setReferenceCounted(false); acquire() }
        val l = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(type: String) {}
            override fun onDiscoveryStopped(type: String) {}
            override fun onStartDiscoveryFailed(type: String, code: Int) { Log.w(TAG, "recherche impossible : $code") }
            override fun onStopDiscoveryFailed(type: String, code: Int) {}
            override fun onServiceFound(info: NsdServiceInfo) = main.post { enqueue(info) }.let {}
            override fun onServiceLost(info: NsdServiceInfo) = main.post {
                if (boxes.remove(info.serviceName) != null) publish()
            }.let {}
        }
        listener = l
        nsd.discoverServices(SERVICE, NsdManager.PROTOCOL_DNS_SD, l)
    }

    fun stop() {
        listener?.let { try { nsd.stopServiceDiscovery(it) } catch (_: Exception) {} }
        listener = null
        lock?.let { try { it.release() } catch (_: Exception) {} }
        lock = null
        toResolve.clear()
    }

    private fun enqueue(info: NsdServiceInfo) {
        if (toResolve.none { it.serviceName == info.serviceName }) toResolve.addLast(info)
        next()
    }

    @Suppress("DEPRECATION")
    private fun next() {
        if (resolving || listener == null) return
        val info = toResolve.removeFirstOrNull() ?: return
        resolving = true
        nsd.resolveService(info, object : NsdManager.ResolveListener {
            override fun onResolveFailed(i: NsdServiceInfo, code: Int) = main.post {
                resolving = false
                next()
            }.let {}

            override fun onServiceResolved(i: NsdServiceInfo) = main.post {
                resolving = false
                val host = i.host?.hostAddress
                if (host != null && i.port > 0 && listener != null) {
                    val txt = i.attributes["name"]?.let { String(it, Charsets.UTF_8) }
                    boxes[i.serviceName] = Box(i.serviceName, boxDisplayName(i.serviceName, txt), host, i.port)
                    publish()
                }
                next()
            }.let {}
        })
    }

    private fun publish() = onChange(boxes.values.sortedBy { it.name.lowercase() })

    companion object {
        const val SERVICE = "_kairocast._tcp"
        private const val TAG = "KairoCastEnvoi"
    }
}
