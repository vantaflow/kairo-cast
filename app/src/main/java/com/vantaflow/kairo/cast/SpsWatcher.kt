package com.vantaflow.kairo.cast

object H264 {
    /** Position du premier octet de NAL (après le start code) à partir de [from], ou -1. */
    private fun nextNal(data: ByteArray, size: Int, from: Int): Int {
        var i = from
        while (i + 2 < size) {
            if (data[i].toInt() == 0 && data[i + 1].toInt() == 0) {
                if (data[i + 2].toInt() == 1) return i + 3
                if (i + 3 < size && data[i + 2].toInt() == 0 && data[i + 3].toInt() == 1) return i + 4
            }
            i++
        }
        return -1
    }

    private inline fun forEachNal(data: ByteArray, size: Int, action: (start: Int, end: Int) -> Boolean) {
        val limit = minOf(size, data.size)
        var start = nextNal(data, limit, 0)
        while (start in 0 until limit) {
            val next = nextNal(data, limit, start)
            var end = if (next < 0) limit else next - 3
            if (next >= 0 && end > start && data[end - 1].toInt() == 0) end-- // start code sur 4 octets
            if (action(start, end)) return
            start = next
        }
    }

    fun isKeyFrame(data: ByteArray, size: Int): Boolean {
        var key = false
        forEachNal(data, size) { s, _ ->
            val type = data[s].toInt() and 0x1F
            key = type == 5 || type == 7
            key
        }
        return key
    }

    /** Premier NAL du type donné (octet d'en-tête inclus, sans start code), ou null. */
    fun extractNal(data: ByteArray, size: Int, type: Int): ByteArray? {
        var nal: ByteArray? = null
        forEachNal(data, size) { s, e ->
            if ((data[s].toInt() and 0x1F) == type) {
                nal = data.copyOfRange(s, e)
                true
            } else false
        }
        return nal
    }

    fun extractPps(data: ByteArray, size: Int): ByteArray? = extractNal(data, size, 8)

    /** Vrai si le lot ne contient que des SPS/PPS (au moins un). */
    fun isConfigOnly(data: ByteArray, size: Int): Boolean {
        var any = false
        var other = false
        forEachNal(data, size) { s, _ ->
            val t = data[s].toInt() and 0x1F
            if (t == 7 || t == 8) any = true else other = true
            other
        }
        return any && !other
    }

    /** Vrai si le lot contient une image IDR (NAL de type 5). */
    fun hasIdr(data: ByteArray, size: Int): Boolean {
        var idr = false
        forEachNal(data, size) { s, _ ->
            idr = (data[s].toInt() and 0x1F) == 5
            idr
        }
        return idr
    }

    /** NAL SPS (octet d'en-tête inclus, sans start code), ou null. */
    fun extractSps(data: ByteArray, size: Int): ByteArray? {
        var sps: ByteArray? = null
        forEachNal(data, size) { s, e ->
            if ((data[s].toInt() and 0x1F) == 7) {
                sps = data.copyOfRange(s, e)
                true
            } else false
        }
        return sps
    }
}

class SpsWatcher {
    private var last: ByteArray? = null

    @Synchronized
    fun changed(data: ByteArray, size: Int): Boolean {
        val sps = H264.extractSps(data, size) ?: return false
        if (last?.contentEquals(sps) == true) return false
        last = sps
        return true
    }
}
