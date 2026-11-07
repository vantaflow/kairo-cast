package com.vantaflow.kairo.cast

import android.util.Log
import android.view.Surface

/** Reçoit les rappels natifs (via MirrorBus) et alimente un décodeur lié à la surface. */
class MirrorPipeline(
    surface: Surface,
    initialSize: Pair<Int, Int>?,
    private val onFormat: (Int, Int, Int, Int, Int, Int) -> Unit,
) : ReceiverListener {
    @Volatile private var decoder: VideoDecoder? = VideoDecoder(surface).also { d ->
        initialSize?.let { d.setVideoSize(it.first, it.second) }
    }
    private var drops = 0L

    fun release() {
        val d = decoder
        decoder = null
        d?.release()
    }

    override fun onConnectionOpened() {
        Log.i(TAG, "connexion ouverte (pipeline)")
    }

    override fun onConnectionClosed() {
        Log.i(TAG, "connexion fermée (pipeline)")
    }

    override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) {
        Log.i(TAG, "onVideoFormat flux ${width}x$height contenu x=$contentX y=$contentY ${contentW}x$contentH")
        decoder?.setVideoSize(width, height)
        onFormat(width, height, contentX, contentY, contentW, contentH)
    }

    override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) {
        val d = decoder
        if (d == null || size <= 0) {
            if (++drops % 100 == 1L) Log.w(TAG, "image abandonnée (décodeur absent), total $drops")
            return
        }
        // `data` est réutilisé par le thread natif : copie obligatoire.
        d.feed(Frame(data.copyOf(size), size, ptsUs, H264.isKeyFrame(data, size)))
    }

    override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long) {}

    override fun onAudioVolume(volumeDb: Float) {}

    override fun onVideoReset() {
        Log.i(TAG, "onVideoReset : nouveau décodeur")
        val old = decoder ?: return
        // Décodeur neuf (SpsWatcher et file neufs) : un SPS identique reconfigure bien.
        val surface = old.surface
        old.release()
        decoder = VideoDecoder(surface).also { it.setVideoSize(old.currentWidth, old.currentHeight) }
    }

    private companion object {
        const val TAG = "KairoCast"
    }
}
