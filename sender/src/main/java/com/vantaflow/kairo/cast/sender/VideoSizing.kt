package com.vantaflow.kairo.cast.sender

/** Taille de l'image envoyée : celle de l'écran, réduite pour tenir dans [maxLong]×[maxShort], multiple de 16. */
object VideoSizing {
    /** Côtés longs essayés, du plus net au plus sûr (certains encodeurs refusent 1920 en portrait). */
    val LONG_SIDES = intArrayOf(1920, 1280, 960)

    fun fit(screenW: Int, screenH: Int, maxLong: Int, maxShort: Int = maxLong * 9 / 16): Pair<Int, Int> {
        require(screenW > 0 && screenH > 0) { "taille d'écran invalide" }
        val landscape = screenW >= screenH
        val long = if (landscape) screenW else screenH
        val short = if (landscape) screenH else screenW
        val scale = minOf(1.0, maxLong.toDouble() / long, maxShort.toDouble() / short)
        val l = align(long * scale)
        val s = align(short * scale)
        return if (landscape) l to s else s to l
    }

    private fun align(v: Double): Int = maxOf(16, (v.toInt() / 16) * 16)

    /** Débit conseillé (bit/s) : environ 0,12 bit par pixel à 30 i/s, entre 2 et 10 Mbit/s. */
    fun bitrate(w: Int, h: Int, fps: Int = 30): Int =
        (w.toLong() * h * fps * 12 / 100).coerceIn(2_000_000, 10_000_000).toInt()
}
