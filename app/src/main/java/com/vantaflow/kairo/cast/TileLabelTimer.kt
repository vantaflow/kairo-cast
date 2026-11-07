package com.vantaflow.kairo.cast

/** Visibilité temporaire des étiquettes : affichées [durationMs] après une arrivée ou un changement de focus. */
class TileLabelTimer(
    private val durationMs: Long = 4000,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val until = HashMap<Int, Long>()

    fun touch(slot: Int) { until[slot] = clock() + durationMs }

    fun forget(slot: Int) { until.remove(slot) }

    fun visible(slot: Int): Boolean = (until[slot] ?: 0L) > clock()
}
