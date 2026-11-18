package com.vantaflow.kairo.cast.sender

import com.vantaflow.kairo.cast.link.LinkCodec
import com.vantaflow.kairo.cast.link.LinkFrame
import com.vantaflow.kairo.cast.link.LinkType
import java.net.InetSocketAddress
import java.net.Socket
import org.json.JSONObject

/**
 * État d'une source de la box : libre, ou occupée par un Mac/iPhone (AirPlay) ou un appareil Kairo Link.
 * Obtenu par une trame STATUS, sans toucher à la recopie en cours. Une box trop ancienne ne répond pas : null.
 */
sealed interface Occupancy {
    object Free : Occupancy
    /** Occupée par un appareil Kairo Link nommé. */
    data class ByDevice(val name: String) : Occupancy
    /** Occupée par un Mac ou un iPhone (AirPlay). */
    object ByApple : Occupancy
    object Busy : Occupancy
}

data class BoxStatus(val busy: Boolean, val source: String, val by: String) {
    /** Occupation de la source ; le texte est choisi à l'affichage, dans la langue de l'appareil. */
    fun occupancy(): Occupancy = when {
        !busy -> Occupancy.Free
        source == "link" && by.isNotEmpty() -> Occupancy.ByDevice(by)
        source == "airplay" -> Occupancy.ByApple
        else -> Occupancy.Busy
    }

    companion object {
        fun parse(bytes: ByteArray): BoxStatus? = try {
            val o = JSONObject(String(bytes, Charsets.UTF_8))
            val b = o.opt("busy")
            if (b !is Boolean) null else BoxStatus(b, o.optString("source", ""), o.optString("by", ""))
        } catch (_: Exception) {
            null
        }

        /** Pose la question STATUS ; null si la box ne répond pas à temps ou ne connaît pas STATUS. */
        fun query(host: String, port: Int, timeoutMs: Int = 2000): BoxStatus? = try {
            Socket().use { s ->
                s.connect(InetSocketAddress(host, port), timeoutMs)
                s.soTimeout = timeoutMs
                s.getOutputStream().apply { write(LinkCodec.encode(LinkFrame(LinkType.STATUS, 0, 0, ByteArray(0)))); flush() }
                val input = s.getInputStream().buffered()
                var answer: BoxStatus? = null
                while (true) {
                    val f = LinkCodec.read(input, 4096) ?: break
                    if (f.type == LinkType.STATUS) { answer = parse(f.payload); break }
                }
                answer
            }
        } catch (_: Exception) {
            null
        }
    }
}
