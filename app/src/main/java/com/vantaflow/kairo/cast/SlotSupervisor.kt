package com.vantaflow.kairo.cast

/** Ce que le superviseur démarre et arrête pour un emplacement. */
interface SlotRunner {
    fun start(): Boolean
    fun stop()
}

/**
 * Réconcilie les emplacements voulus avec ceux qui tournent. Une clé par emplacement résume tout ce
 * qui impose un redémarrage (nom, résolution annoncée…) : seuls les emplacements dont la clé change
 * sont redémarrés. Pur, à appeler depuis l'unique thread de travail du service.
 */
class SlotSupervisor(private val factory: (slot: Int) -> SlotRunner) {
    private class Entry(val key: Any, val runner: SlotRunner, var ok: Boolean)

    private val running = sortedMapOf<Int, Entry>()

    val slots: List<Int> get() = running.keys.toList()

    /** Un emplacement dont le démarrage a échoué est retenté à l'appel suivant (réglages validés à nouveau). */
    fun apply(keys: List<Any>) {
        running.keys.filter { it >= keys.size }.forEach { stopSlot(it) }
        keys.forEachIndexed { slot, key ->
            val cur = running[slot]
            if (cur != null && cur.key == key && cur.ok) return@forEachIndexed
            if (cur != null) stopSlot(slot)
            val runner = factory(slot)
            val entry = Entry(key, runner, false)
            running[slot] = entry
            // Isolé : un échec (ou une exception) n'arrête jamais les autres emplacements.
            entry.ok = try { runner.start() } catch (e: Exception) { false }
        }
    }

    fun stopAll() {
        running.keys.toList().forEach { stopSlot(it) }
    }

    private fun stopSlot(slot: Int) {
        val e = running.remove(slot) ?: return
        try { e.runner.stop() } catch (_: Exception) {}
    }
}

/** Clés de redémarrage : la résolution annoncée ne dépend que de 1 / 2 / ≥3 sources, pas du nombre exact. */
object SlotKeys {
    fun of(names: List<String>, quality: StreamQuality, audio: Boolean): List<Any> {
        val n = names.size
        return names.map { listOf(it, SlotPolicy.modeBucket(n), if (n == 1) quality else null, audio) }
    }
}
