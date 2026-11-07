package com.vantaflow.kairo.cast

/** Choisit la source audible : premier actif, repli sur le plus petit, puis suit le focus après un délai. */
class AudioSelector(
    private val focusDelayMs: Long = 1000,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    var listener: (Int?) -> Unit = {}

    var audioSlot: Int? = null
        private set

    private var active: List<Int> = emptyList()
    private var pendingSlot: Int? = null
    private var pendingSince = 0L

    fun onActiveChanged(active: List<Int>) {
        notify(synchronized(this) {
            this.active = active.distinct().sorted()
            if (pendingSlot !in this.active) pendingSlot = null
            if (audioSlot == null || audioSlot !in this.active) set(this.active.firstOrNull()) else NONE
        })
    }

    @Synchronized
    fun onFocus(slot: Int) {
        if (slot !in active) return
        if (slot == audioSlot) {
            pendingSlot = null
        } else if (slot != pendingSlot) {
            pendingSlot = slot
            pendingSince = clock()
        }
    }

    fun tick() {
        notify(synchronized(this) {
            val p = pendingSlot
            if (p != null && clock() - pendingSince >= focusDelayMs) {
                pendingSlot = null
                set(p)
            } else NONE
        })
    }

    /** Applique le changement sous verrou ; l'appelant notifie l'écouteur hors verrou. */
    private fun set(slot: Int?): Change {
        if (slot == audioSlot) return NONE
        audioSlot = slot
        return Change(slot)
    }

    private class Change(val slot: Int?)

    private fun notify(c: Change) {
        if (c !== NONE) listener(c.slot)
    }

    private companion object { val NONE = Change(null) }
}
