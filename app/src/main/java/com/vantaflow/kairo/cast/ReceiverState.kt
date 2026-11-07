package com.vantaflow.kairo.cast

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

sealed interface Status {
    object Stopped : Status
    object Waiting : Status
    /** Session Link finie alors que le Mac est resté connecté : il doit relancer la recopie. */
    object MacWaiting : Status
    data class Mirroring(val width: Int, val height: Int) : Status
}

object ReceiverState {
    private val _status = MutableStateFlow<Status>(Status.Stopped)
    val status: StateFlow<Status> = _status

    fun set(s: Status) {
        _status.value = s
    }
}

/** Une valeur par emplacement ; remplacer ou retirer libère l'ancienne valeur. Testable sans Android. */
class SlotRegistry<T : Any>(private val release: (T) -> Unit, private val create: (Int) -> T) {
    private val items = arrayOfNulls<Any>(MAX_SLOTS)

    @Suppress("UNCHECKED_CAST")
    @Synchronized
    fun get(slot: Int): T? = items[slot] as T?

    @Suppress("UNCHECKED_CAST")
    @Synchronized
    fun start(slot: Int): T {
        (items[slot] as T?)?.let(release)
        return create(slot).also { items[slot] = it }
    }

    @Suppress("UNCHECKED_CAST")
    @Synchronized
    fun stop(slot: Int) {
        (items[slot] as T?)?.let(release)
        items[slot] = null
    }

    @Synchronized
    fun stopAll() = (0 until MAX_SLOTS).forEach { stop(it) }

    @Synchronized
    fun firstSlot(): Int? = (0 until MAX_SLOTS).firstOrNull { items[it] != null }
}

/**
 * Pipelines vidéo, un par emplacement, qui vivent aussi longtemps que la session de recopie de
 * l'emplacement (et non l'activité). L'activité ne fait que brancher / débrancher sa surface.
 */
object MirrorBus {
    private var audioEnabled = true
    private var onKey: Array<() -> Unit> = Array(MAX_SLOTS) { {} }
    private val registry = SlotRegistry<MirrorPipeline>({ it.release() }) { MirrorPipeline(audioEnabled, onKey[it], audible = MirrorAudio.selector.audioSlot == it) }

    fun pipeline(slot: Int): MirrorPipeline? = registry.get(slot)

    /** Pipeline du plus petit emplacement ouvert (écran à une seule tuile). */
    val pipeline: MirrorPipeline?
        get() = registry.firstSlot()?.let { registry.get(it) }

    @Synchronized
    fun start(slot: Int, audioEnabled: Boolean = true, onKeyFrameNeeded: () -> Unit = {}) {
        this.audioEnabled = audioEnabled
        onKey[slot] = onKeyFrameNeeded
        registry.start(slot)
    }

    fun stop(slot: Int) = registry.stop(slot)

    fun stopAll() = registry.stopAll()
}
