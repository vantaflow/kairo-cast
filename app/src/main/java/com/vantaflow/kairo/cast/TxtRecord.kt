package com.vantaflow.kairo.cast

/** Décodage d'un enregistrement DNS TXT : suite de [longueur][clé=valeur]. */
object TxtRecord {
    fun parse(raw: ByteArray): Map<String, String> {
        val out = LinkedHashMap<String, String>()
        var i = 0
        while (i < raw.size) {
            val len = raw[i].toInt() and 0xFF
            i++
            if (len > raw.size - i) break
            if (len > 0) {
                val entry = String(raw, i, len, Charsets.UTF_8)
                val eq = entry.indexOf('=')
                if (eq < 0) out[entry] = "" else out[entry.substring(0, eq)] = entry.substring(eq + 1)
            }
            i += len
        }
        return out
    }
}
