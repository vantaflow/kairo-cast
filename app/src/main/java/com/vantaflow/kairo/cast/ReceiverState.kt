package com.vantaflow.kairo.cast

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

sealed interface Status {
    object Stopped : Status
    object Waiting : Status
    data class Mirroring(val width: Int, val height: Int) : Status
}

object ReceiverState {
    private val _status = MutableStateFlow<Status>(Status.Stopped)
    val status: StateFlow<Status> = _status

    fun set(s: Status) {
        _status.value = s
    }
}

/** Relais vers le pipeline d'affichage (Task 7). */
object MirrorBus {
    @Volatile
    var sink: ReceiverListener? = null
}
