package com.vantaflow.kairo.cast.debug

/** Découpage d'un flux H.264 Annex-B en unités d'accès (harnais de mesure, debug uniquement). */
object AnnexB {
    /**
     * Une plage par unité d'accès. Une unité commence à un SPS, PPS, SEI, AUD ou à un slice dont
     * first_mb_in_slice == 0, dès que l'unité courante contient déjà un slice. SPS/PPS restent donc
     * collés à l'IDR qui suit. Sans start code : une seule plage couvrant tout le flux.
     */
    fun splitAccessUnits(data: ByteArray): List<IntRange> {
        if (data.isEmpty()) return emptyList()
        // Positions des start codes (début du préfixe 00 00 01, éventuellement précédé d'un 00).
        val starts = ArrayList<Int>() // début du préfixe
        val nalPos = ArrayList<Int>() // premier octet du NAL
        var i = 0
        while (i + 2 < data.size) {
            if (data[i].toInt() == 0 && data[i + 1].toInt() == 0 && data[i + 2].toInt() == 1) {
                val s = if (i > 0 && data[i - 1].toInt() == 0) i - 1 else i
                starts.add(s)
                nalPos.add(i + 3)
                i += 3
            } else i++
        }
        if (starts.isEmpty()) return listOf(0 until data.size)
        val result = ArrayList<IntRange>()
        var auStart = 0
        var hasSlice = false
        for (k in starts.indices) {
            val p = nalPos[k]
            if (p >= data.size) break
            val type = data[p].toInt() and 0x1F
            val isSlice = type == 1 || type == 5
            val startsNew = when {
                isSlice -> hasSlice && p + 1 < data.size && (data[p + 1].toInt() and 0x80) != 0
                type in 6..9 -> hasSlice
                else -> false
            }
            if (startsNew) {
                result.add(auStart until starts[k])
                auStart = starts[k]
                hasSlice = false
            }
            if (isSlice) hasSlice = true
        }
        result.add(auStart until data.size)
        return result
    }
}
