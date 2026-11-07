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

/**
 * Point d'accès au pipeline vidéo, qui vit aussi longtemps que la session de recopie (et non
 * l'activité). L'activité ne fait que brancher / débrancher sa surface.
 */
object MirrorBus {
    @Volatile
    var pipeline: MirrorPipeline? = null
        private set

    @Synchronized
    fun start(audioEnabled: Boolean = true, onKeyFrameNeeded: () -> Unit = {}) {
        pipeline?.release()
        pipeline = MirrorPipeline(audioEnabled, onKeyFrameNeeded)
    }

    @Synchronized
    fun stop() {
        pipeline?.release()
        pipeline = null
    }
}
