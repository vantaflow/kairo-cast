package com.vantaflow.kairo.cast

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

enum class SlotPhase { STOPPED, WAITING, MAC_WAITING, MIRRORING, SATURATED, ERROR }

data class SlotState(val slot: Int, val name: String, val phase: SlotPhase, val width: Int = 0, val height: Int = 0)

/** État de tous les emplacements ; [ReceiverState.status] en est dérivé pour l'écran d'accueil. */
object SlotsState {
    private val _slots = MutableStateFlow<List<SlotState>>(emptyList())
    val slots: StateFlow<List<SlotState>> = _slots

    private val _active = MutableStateFlow<List<Int>>(emptyList())
    /** Emplacements qui occupent l'écran (MIRRORING et SATURATED), triés. */
    val active: StateFlow<List<Int>> = _active

    fun activeOf(list: List<SlotState>): List<Int> =
        list.filter { it.phase == SlotPhase.MIRRORING || it.phase == SlotPhase.SATURATED }.map { it.slot }.sorted()

    /** L'écran de recopie se ferme quand le dernier emplacement actif part. */
    fun shouldClose(prev: List<Int>, next: List<Int>): Boolean = prev.isNotEmpty() && next.isEmpty()

    fun statusOf(list: List<SlotState>): Status {
        if (list.isEmpty()) return Status.Stopped
        list.sortedBy { it.slot }.firstOrNull { it.phase == SlotPhase.MIRRORING }
            ?.let { return Status.Mirroring(it.width, it.height) }
        if (list.any { it.slot == 0 && it.phase == SlotPhase.MAC_WAITING }) return Status.MacWaiting
        if (list.all { it.phase == SlotPhase.STOPPED || it.phase == SlotPhase.ERROR }) return Status.Stopped
        return Status.Waiting
    }

    @Synchronized
    fun update(state: SlotState) {
        val cur = _slots.value
        val next = if (cur.any { it.slot == state.slot }) cur.map { if (it.slot == state.slot) state else it }
        else (cur + state).sortedBy { it.slot }
        publish(next)
    }

    /** Un emplacement quitte la liste (nombre d'emplacements réduit). */
    @Synchronized
    fun remove(slot: Int) = publish(_slots.value.filter { it.slot != slot })

    @Synchronized
    fun reset(count: Int, names: List<String>) =
        publish((0 until count).map { SlotState(it, names.getOrElse(it) { SlotIdentity.defaultName(it) }, SlotPhase.STOPPED) })

    private fun publish(list: List<SlotState>) {
        _slots.value = list
        _active.value = activeOf(list)
        ReceiverState.set(statusOf(list))
    }
}
