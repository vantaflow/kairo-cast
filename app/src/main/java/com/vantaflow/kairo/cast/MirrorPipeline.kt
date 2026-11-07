package com.vantaflow.kairo.cast

import android.util.Log
import android.view.Surface

/**
 * Pipeline vidéo de la session de recopie : possède le décodeur, qui continue de tourner même
 * sans activité (sortie sur une surface de remplacement). Créé à l'ouverture de la connexion,
 * libéré à sa fermeture.
 */
class MirrorPipeline : ReceiverListener {
    private val lock = Any()
    private val placeholder = PlaceholderSurface()
    private var released = false
    private var decoder: VideoDecoder? = null
    private var outputSurface: Surface? = null // surface de l'activité, ou null
    private var format: IntArray? = null
    private var formatListener: ((IntArray) -> Unit)? = null
    private var drops = 0L

    private fun targetSurface(): Surface? = outputSurface ?: placeholder.surface

    /** Branche la surface de l'activité ; [listener] reçoit (immédiatement si connu) le dernier format. */
    fun attach(surface: Surface, listener: (IntArray) -> Unit) {
        val known: IntArray?
        synchronized(lock) {
            if (released) return
            outputSurface = surface
            formatListener = listener
            decoder?.setOutputSurface(surface)
            known = format
        }
        Log.i(TAG, "surface de l'activité branchée")
        known?.let(listener)
    }

    /** Débranche la surface de l'activité ; attend que le décodeur soit passé sur la surface de remplacement. */
    fun detach(listener: (IntArray) -> Unit) {
        val d: VideoDecoder?
        val ph: Surface?
        synchronized(lock) {
            if (formatListener !== listener) return
            formatListener = null
            outputSurface = null
            d = decoder
            ph = placeholder.surface
        }
        if (d != null && ph != null) d.setOutputSurface(ph, 500)
        Log.i(TAG, "surface de l'activité débranchée")
    }

    fun release() {
        val d: VideoDecoder?
        synchronized(lock) {
            if (released) return
            released = true
            d = decoder
            decoder = null
            formatListener = null
            outputSurface = null
        }
        Thread({
            d?.release()
            placeholder.release()
        }, "kairo-release").start()
    }

    private fun decoderLocked(): VideoDecoder? {
        if (released) return null
        decoder?.let { return it }
        val s = targetSurface() ?: return null
        return VideoDecoder(s).also { d ->
            format?.let { d.setVideoSize(it[0], it[1]) }
            decoder = d
        }
    }

    override fun onConnectionOpened() {}

    override fun onConnectionClosed() {}

    override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) {
        val f = intArrayOf(width, height, contentX, contentY, contentW, contentH)
        val l: ((IntArray) -> Unit)?
        synchronized(lock) {
            if (released) return
            format = f
            decoderLocked()?.setVideoSize(width, height)
            l = formatListener
        }
        l?.invoke(f)
    }

    override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) {
        val d = synchronized(lock) { decoderLocked() }
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
        Log.i(TAG, "onVideoReset : nouveau décodeur au prochain flux")
        val old: VideoDecoder?
        synchronized(lock) {
            if (released) return
            old = decoder
            decoder = null // recréé paresseusement (SpsWatcher et file neufs)
        }
        // Jamais de join sur le thread natif.
        if (old != null) Thread({ old.release() }, "kairo-release").start()
    }

    private companion object {
        const val TAG = "KairoCast"
    }
}
