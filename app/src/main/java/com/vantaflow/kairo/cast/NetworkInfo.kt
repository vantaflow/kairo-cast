package com.vantaflow.kairo.cast

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import java.net.Inet4Address

object NetworkInfo {
    /** Adresse IPv4 locale (Ethernet ou Wi-Fi), ou null si la box n'est pas connectée. */
    fun localIpv4(ctx: Context): String? {
        val cm = ctx.getSystemService(ConnectivityManager::class.java) ?: return null
        val candidates = cm.allNetworks.mapNotNull { n ->
            val caps = cm.getNetworkCapabilities(n) ?: return@mapNotNull null
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) return@mapNotNull null
            val rank = when {
                caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> 0
                caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> 1
                else -> return@mapNotNull null
            }
            val ip = cm.getLinkProperties(n)?.linkAddresses
                ?.map { it.address }?.filterIsInstance<Inet4Address>()
                ?.firstOrNull { !it.isLoopbackAddress && !it.isLinkLocalAddress }
                ?.hostAddress ?: return@mapNotNull null
            rank to ip
        }
        return candidates.minByOrNull { it.first }?.second
    }
}
