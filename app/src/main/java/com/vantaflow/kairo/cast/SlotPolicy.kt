package com.vantaflow.kairo.cast

data class AnnouncedMode(val width: Int, val height: Int, val maxFps: Int)

sealed interface Admission {
    object Accepted : Admission
    object Saturated : Admission
}

/** Résolutions annoncées en 16:10 (le Mac réduit sa propre résolution pour s'adapter),  budget de décodage partagé et saturation par emplacement. */
class SlotPolicy(
    slotCount: Int,
    quality: StreamQuality,
    val budgetCap: Int = DEFAULT_BUDGET,
) {
    @Volatile var slotCount: Int = slotCount
        private set
    @Volatile private var quality: StreamQuality = quality

    /** Met à jour nombre et qualité en place : les budgets déjà admis sont conservés. */
    fun configure(slotCount: Int, quality: StreamQuality) {
        this.slotCount = slotCount
        this.quality = quality
    }

    private val admitted = LinkedHashMap<Int, Long>()
    private val saturated = LinkedHashMap<Int, Long>()

    fun announced(): AnnouncedMode = when {
        slotCount <= 1 -> AnnouncedMode(quality.width, quality.height, FPS)
        slotCount == 2 -> AnnouncedMode(2880, 1800, FPS)
        else -> AnnouncedMode(1280, 800, FPS)
    }

    fun costOf(width: Int, height: Int): Long = width.toLong() * height.toLong() / 256 * 30

    @Synchronized
    fun admit(slot: Int, width: Int, height: Int): Admission {
        admitted.remove(slot)
        saturated.remove(slot)
        if (!isValidSize(width, height)) return Admission.Saturated // taille absurde : refusée, jamais reprise
        val cost = costOf(width, height)
        // Une source seule est toujours admise à la qualité choisie ; le budget joue dès 2 sources.
        return if (admitted.isEmpty() || admitted.values.sum() + cost <= budgetCap.toLong()) {
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
            if (admitted.values.sum() + cost <= budgetCap.toLong()) {
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
        fun modeBucket(slotCount: Int): Int = if (slotCount <= 1) 1 else if (slotCount == 2) 2 else 3
        const val MAX_DIMENSION = 4096

        /** Dimensions plausibles : 1..[MAX_DIMENSION] de chaque côté. */
        fun isValidSize(width: Int, height: Int): Boolean =
            width in 1..MAX_DIMENSION && height in 1..MAX_DIMENSION

        const val DEFAULT_BUDGET = 829_440
        const val MESSAGE_SATURATED = "Trop de sources pour cette box"
        private const val FPS = 30
    }
}
