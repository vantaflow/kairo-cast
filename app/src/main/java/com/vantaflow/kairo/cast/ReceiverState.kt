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
 * Relais vers le pipeline d'affichage. Garde la dernière configuration (format, SPS/PPS) et la
 * dernière image clé pour les rejouer à un nouveau pipeline (réouverture de l'écran de recopie).
 */
object MirrorBus {
    private val lock = Any()
    private var format: IntArray? = null
    private var config: Frame? = null
    private var key: Frame? = null

    @Volatile
    private var current: ReceiverListener? = null

    var sink: ReceiverListener?
        get() = current
        set(value) {
            if (value != null) {
                val (f, c, k) = synchronized(lock) { Triple(format, config, key) }
                f?.let { value.onVideoFormat(it[0], it[1], it[2], it[3], it[4], it[5]) }
                c?.let { value.onVideoData(it.data, it.size, it.ptsUs) }
                k?.let { value.onVideoData(it.data, it.size, it.ptsUs) }
            }
            current = value
        }

    fun remember(width: Int, height: Int, cx: Int, cy: Int, cw: Int, ch: Int) = synchronized(lock) {
        format = intArrayOf(width, height, cx, cy, cw, ch)
    }

    fun rememberVideo(data: ByteArray, size: Int, ptsUs: Long) {
        if (H264.isConfigOnly(data, size)) {
            synchronized(lock) { config = Frame(data.copyOf(size), size, ptsUs, true) }
        } else if (H264.hasIdr(data, size)) {
            synchronized(lock) { key = Frame(data.copyOf(size), size, ptsUs, true) }
        }
    }

    fun clearCache() = synchronized(lock) {
        format = null
        config = null
        key = null
    }
}
