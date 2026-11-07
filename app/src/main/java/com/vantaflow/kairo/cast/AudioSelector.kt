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

    @Synchronized
    fun onActiveChanged(active: List<Int>) {
        this.active = active.distinct().sorted()
        if (pendingSlot !in this.active) pendingSlot = null
        if (audioSlot == null || audioSlot !in this.active) set(this.active.firstOrNull())
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

    @Synchronized
    fun tick() {
        val p = pendingSlot ?: return
        if (clock() - pendingSince >= focusDelayMs) {
            pendingSlot = null
            set(p)
        }
    }

    private fun set(slot: Int?) {
        if (slot == audioSlot) return
        audioSlot = slot
        listener(slot)
    }
}
