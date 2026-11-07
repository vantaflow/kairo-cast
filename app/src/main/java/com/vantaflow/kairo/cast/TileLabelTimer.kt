package com.vantaflow.kairo.cast

/** Visibilité temporaire des étiquettes : affichées [durationMs] après une arrivée ou un changement de focus. */
class TileLabelTimer(
    private val durationMs: Long = 4000,
    private val fadeMs: Long = 500,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val until = HashMap<Int, Long>()

    fun touch(slot: Int) { until[slot] = clock() + durationMs }

    fun forget(slot: Int) { until.remove(slot) }

    fun visible(slot: Int): Boolean = (until[slot] ?: 0L) > clock()

    /** 1 pendant l'affichage, puis fondu linéaire sur [fadeMs] avant la fin, 0 ensuite. */
    fun alpha(slot: Int): Float {
        val left = (until[slot] ?: 0L) - clock()
        return when {
            left <= 0 -> 0f
            left >= fadeMs -> 1f
            else -> left.toFloat() / fadeMs
        }
    }
}
