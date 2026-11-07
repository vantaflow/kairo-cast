package com.vantaflow.kairo.cast

import android.util.Log
import android.view.Surface

/** Ce que [SlotController] pilote : le pipeline d'un emplacement (fabriquable en test). */
interface PipelineFeed : ReceiverListener {
    fun onPcmAudio(data: ByteArray, size: Int, ptsUs: Long)
    fun onSourceChanged()
    fun setAudioMuted(muted: Boolean)
    /** Appelé quand le décodeur matériel manque de ressources (l'emplacement passe en saturation). */
    var onResourceError: () -> Unit
}

/** Lecteur audio d'un pipeline (fabriquable en test). */
interface AudioSink {
    fun setVolumeDb(db: Float)
    fun start()
    fun startPcm(rate: Int, channels: Int)
    fun feed(data: ByteArray, size: Int, ptsUs: Long)
    fun feedPcm(data: ByteArray, size: Int, ptsUs: Long)
    fun release()
}

/**
 * Pipeline vidéo de la session de recopie : possède le décodeur, qui continue de tourner même
 * sans activité (sortie sur une surface de remplacement). Créé à l'ouverture de la connexion,
 * libéré à sa fermeture.
 */
class MirrorPipeline(
    private val audioEnabled: Boolean = true,
    /** Appelé quand une image clé serait utile (décodeur reconfiguré sans IDR, activité rattachée). */
    private val onKeyFrameNeeded: () -> Unit = {},
    audible: Boolean = true,
    private val audioFactory: (Int) -> AudioSink = { AudioPlayerSink(AudioPlayer(it)) },
) : PipelineFeed {
    @Volatile private var audible = audible
    override var onResourceError: () -> Unit = {}

    /** Coupe le son de ce flux sans toucher au décodeur (un seul emplacement audible à la fois). */
    override fun setAudioMuted(muted: Boolean) = setAudible(!muted)

    /** Rend ce flux audible (le lecteur est créé au prochain paquet) ou le coupe (le lecteur est libéré). */
    fun setAudible(on: Boolean) {
        val old: AudioSink?
        synchronized(lock) {
            if (audible == on) return
            audible = on
            if (on) { old = null } else {
                old = audio
                audio = null
                audioCt = -1
            }
        }
        old?.release()
    }

    private val lock = Any()
    private val placeholderLazy = lazy { PlaceholderSurface() }
    private val placeholder by placeholderLazy
    private var released = false
    private var decoder: VideoDecoder? = null
    private var outputSurface: Surface? = null // surface de l'activité, ou null
    private var format: IntArray? = null
    private var formatListener: ((IntArray) -> Unit)? = null
    private var drops = 0L
    private var audio: AudioSink? = null
    private var audioCt = -1
    private var audioVolumeDb = 0f
    private var audioSkipLogged = false

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
        try { onKeyFrameNeeded() } catch (e: Exception) { Log.w(TAG, "demande d'image clé échouée : $e") }
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
        val a: AudioSink?
        synchronized(lock) {
            if (released) return
            released = true
            d = decoder
            a = audio
            audio = null
            decoder = null
            formatListener = null
            outputSurface = null
        }
        Thread({
            a?.release()
            d?.release()
            if (placeholderLazy.isInitialized()) placeholder.release()
        }, "kairo-release").start()
    }

    private fun decoderLocked(): VideoDecoder? {
        if (released) return null
        decoder?.let { return it }
        val s = targetSurface() ?: return null
        return VideoDecoder(s, onKeyFrameNeeded) { onResourceError() }.also { d ->
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

    override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) {
        if (!audioEnabled || !audible || size <= 0) return
        val a: AudioSink
        synchronized(lock) {
            if (released) return
            if (codecType != audioCt) {
                audioCt = codecType
                Log.i(TAG, "audio : codec ct=$codecType")
                audio?.release()
                audio = null
                if (codecType == AudioPlayer.CT_AAC_ELD || codecType == AudioPlayer.CT_AAC_LC) {
                    audio = audioFactory(codecType).also {
                        it.setVolumeDb(audioVolumeDb)
                        it.start()
                    }
                } else if (!audioSkipLogged) {
                    audioSkipLogged = true
                    Log.w(TAG, "audio : ct=$codecType (ALAC) non pris en charge, pas de son")
                }
            }
            a = audio ?: return
        }
        a.feed(data, size, ptsUs)
    }

    /** PCM s16le 48 kHz stéréo (Kairo Link). */
    override fun onPcmAudio(data: ByteArray, size: Int, ptsUs: Long) {
        if (!audioEnabled || !audible || size <= 0) return
        val a: AudioSink
        synchronized(lock) {
            if (released) return
            if (audioCt != AudioPlayer.CT_PCM) {
                audioCt = AudioPlayer.CT_PCM
                Log.i(TAG, "audio : PCM 48000 Hz stéréo")
                audio?.release()
                audio = audioFactory(AudioPlayer.CT_PCM).also {
                    it.setVolumeDb(audioVolumeDb)
                    it.startPcm(48000, 2)
                }
            }
            a = audio ?: return
        }
        a.feedPcm(data, size, ptsUs)
    }

    /** La source (AirPlay <-> Link) change : décodeur et audio repartent à neuf, la surface reste. */
    override fun onSourceChanged() {
        val a: AudioSink?
        synchronized(lock) {
            a = audio
            audio = null
            audioCt = -1
        }
        a?.release()
        onVideoReset()
    }

    override fun onAudioVolume(volumeDb: Float) {
        synchronized(lock) {
            audioVolumeDb = volumeDb
            audio?.setVolumeDb(volumeDb)
        }
    }

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

/** Adaptateur [AudioPlayer] → [AudioSink]. */
class AudioPlayerSink(private val p: AudioPlayer) : AudioSink {
    override fun setVolumeDb(db: Float) = p.setVolumeDb(db)
    override fun start() { p.start() }
    override fun startPcm(rate: Int, channels: Int) { p.startPcm(rate, channels) }
    override fun feed(data: ByteArray, size: Int, ptsUs: Long) = p.feed(data, size, ptsUs)
    override fun feedPcm(data: ByteArray, size: Int, ptsUs: Long) = p.feedPcm(data, size, ptsUs)
    override fun release() = p.release()
}
