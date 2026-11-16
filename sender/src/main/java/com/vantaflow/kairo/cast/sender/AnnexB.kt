package com.vantaflow.kairo.cast.sender

/** Assemblage des unités d'accès H.264 (Annex-B) envoyées à la box. */
object AnnexB {
    /**
     * Le décodeur de la box doit pouvoir repartir de n'importe quelle image clé : SPS/PPS
     * ([config], sortis à part par MediaCodec) sont recopiés devant chaque image clé.
     */
    fun accessUnit(config: ByteArray?, frame: ByteArray, key: Boolean): ByteArray =
        if (!key || config == null || config.isEmpty() || startsWithSps(frame)) frame else config + frame

    /** Vrai si le premier NAL du tampon est un SPS (type 7). */
    fun startsWithSps(b: ByteArray): Boolean {
        val p = when {
            b.size > 4 && b[0] == 0.toByte() && b[1] == 0.toByte() && b[2] == 0.toByte() && b[3] == 1.toByte() -> 4
            b.size > 3 && b[0] == 0.toByte() && b[1] == 0.toByte() && b[2] == 1.toByte() -> 3
            else -> return false
        }
        return (b[p].toInt() and 0x1F) == 7
    }
}
