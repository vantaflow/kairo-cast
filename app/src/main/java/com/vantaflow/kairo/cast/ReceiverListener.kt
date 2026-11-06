package com.vantaflow.kairo.cast

/** Rappels du serveur natif. Appelés depuis des threads natifs (pas le thread principal). */
interface ReceiverListener {
    fun onConnectionOpened()
    fun onConnectionClosed()
    fun onVideoFormat(width: Int, height: Int)
    fun onVideoData(data: ByteArray, size: Int, ptsUs: Long)
    fun onAudioData(data: ByteArray, size: Int, ptsUs: Long)
    fun onAudioVolume(volumeDb: Float)
    fun onVideoReset()
}
