package com.vantaflow.kairo.cast

/** Choisit la source audible : premier actif, repli sur le plus petit, puis suit le focus après un délai. */
class AudioSelector(
    private val focusDelayMs: Long = 1000,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    var listener: (Int?) -> Unit = {}

    @Volatile var audioSlot: Int? = null
        private set

    /** Emplacement épinglé par l'utilisateur ; null = le son suit le focus. Conservé même si l'emplacement s'éteint. */
    var pinned: Int? = null
        private set

    private var active: List<Int> = emptyList()
    private var pendingSlot: Int? = null
    private var pendingSince = 0L

    /** Nouveau démarrage du service : plus de source, plus de focus en attente. */
    @Synchronized
    fun reset() {
        audioSlot = null
        active = emptyList()
        pendingSlot = null
    }

    fun onActiveChanged(active: List<Int>) {
        notify(synchronized(this) {
            this.active = active.distinct().sorted()
            if (pendingSlot !in this.active) pendingSlot = null
            settle()
        })
    }

    /** Épingle (ou libère avec null) la source audible. */
    fun setPinned(slot: Int?) {
        notify(synchronized(this) {
            pinned = slot
            pendingSlot = null
            settle()
        })
    }

    /** Source épinglée active, sinon source courante si elle est toujours active, sinon la première. */
    private fun settle(): Change {
        val p = pinned
        if (p != null && p in active) return set(p)
        return if (audioSlot == null || audioSlot !in active) set(active.firstOrNull()) else NONE
    }

    @Synchronized
    fun onFocus(slot: Int) {
        if (slot !in active || pinned != null) return
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

/** Sélecteur audio unique, partagé entre le service (arrivées/départs) et l'écran de recopie (focus). */
object MirrorAudio {
    val selector = AudioSelector()
}
