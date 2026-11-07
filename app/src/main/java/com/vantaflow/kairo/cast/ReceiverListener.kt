package com.vantaflow.kairo.cast

/** Rappels du serveur natif. Appelés depuis des threads natifs (pas le thread principal). */
interface ReceiverListener {
    fun onConnectionOpened()
    fun onConnectionClosed()
    /** Taille du flux (width x height) et rectangle utile (contentX/Y/W/H) dans l'image. */
    fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int)
    fun onVideoData(data: ByteArray, size: Int, ptsUs: Long)
    fun onAudioData(data: ByteArray, size: Int, ptsUs: Long)
    fun onAudioVolume(volumeDb: Float)
    fun onVideoReset()
}
