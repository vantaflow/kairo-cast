package com.vantaflow.kairo.cast

data class AnnouncedMode(val width: Int, val height: Int, val maxFps: Int)

sealed interface Admission {
    object Accepted : Admission
    object Saturated : Admission
}

/** Résolution annoncée, budget de décodage partagé et saturation par emplacement. */
class SlotPolicy(
    val slotCount: Int,
    private val quality: StreamQuality,
    val budgetCap: Int = DEFAULT_BUDGET,
) {
    private val admitted = LinkedHashMap<Int, Int>()
    private val saturated = LinkedHashMap<Int, Int>()

    fun announced(): AnnouncedMode = when {
        slotCount <= 1 -> AnnouncedMode(quality.width, quality.height, FPS)
        slotCount == 2 -> AnnouncedMode(1920, 1080, FPS)
        else -> AnnouncedMode(1280, 720, FPS)
    }

    fun costOf(width: Int, height: Int): Int = width * height / 256 * 30

    @Synchronized
    fun admit(slot: Int, width: Int, height: Int): Admission {
        admitted.remove(slot)
        saturated.remove(slot)
        val cost = costOf(width, height)
        // Une source seule est toujours admise à la qualité choisie ; le budget joue dès 2 sources.
        return if (admitted.isEmpty() || admitted.values.sum() + cost <= budgetCap) {
            admitted[slot] = cost
            Admission.Accepted
        } else {
            saturated[slot] = cost
            Admission.Saturated
        }
    }

    @Synchronized
    fun release(slot: Int): List<Int> {
        saturated.remove(slot)
        if (admitted.remove(slot) == null) return emptyList()
        val freed = ArrayList<Int>()
        val it = saturated.entries.iterator()
        while (it.hasNext()) {
            val (s, cost) = it.next()
            if (admitted.values.sum() + cost <= budgetCap) {
                admitted[s] = cost
                freed += s
                it.remove()
            }
        }
        return freed
    }

    @Synchronized
    fun isSaturated(slot: Int): Boolean = slot in saturated

    companion object {
        const val DEFAULT_BUDGET = 829_440
        const val MESSAGE_SATURATED = "Trop de sources pour cette box"
        private const val FPS = 30
    }
}
