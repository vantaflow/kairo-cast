package com.vantaflow.kairo.cast

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import android.view.Surface

/**
 * Décodeur H.264 sur un thread dédié. Une instance = une session : SpsWatcher et file neufs.
 * [feed] peut être appelé depuis n'importe quel thread (la file est bornée).
 */
class VideoDecoder(val surface: Surface) {
    private val queue = FrameQueue(QUEUE_CAPACITY)
    private var sps: ByteArray? = null // thread décodeur uniquement
    private var pps: ByteArray? = null
    private val watcher = SpsWatcher()

    @Volatile private var width = 1920
    @Volatile private var height = 1080
    val currentWidth: Int get() = width
    val currentHeight: Int get() = height
    @Volatile private var running = true
    private var codec: MediaCodec? = null // thread décodeur uniquement
    private var framesRendered = 0L
    private var firstFrameLogged = false
    private val thread = Thread(::run, "kairo-video-decoder")

    init {
        thread.start()
    }

    fun setVideoSize(w: Int, h: Int) {
        if (w > 0 && h > 0) {
            width = w
            height = h
        }
    }

    fun feed(frame: Frame) {
        if (running) queue.offer(frame)
    }

    fun release() {
        running = false
        thread.interrupt()
        try {
            thread.join(2000)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }
        queue.clear()
    }

    private fun run() {
        try {
            while (running) {
                val frame = try {
                    queue.poll(10)
                } catch (_: InterruptedException) {
                    break
                }
                if (frame != null) {
                    try {
                        handle(frame)
                    } catch (e: Exception) {
                        Log.e(TAG, "erreur décodeur, réinitialisation", e)
                        recover()
                    }
                }
                try {
                    drain()
                } catch (e: Exception) {
                    Log.e(TAG, "erreur de sortie décodeur, réinitialisation", e)
                    recover()
                }
            }
        } finally {
            resetCodec()
        }
    }

    private fun recover() {
        resetCodec()
        if (sps != null && running) {
            try {
                configure()
            } catch (e: Exception) {
                Log.e(TAG, "reconfiguration impossible", e)
                resetCodec()
            }
        }
    }

    private fun handle(frame: Frame) {
        H264.extractSps(frame.data, frame.size)?.let { sps = it }
        H264.extractPps(frame.data, frame.size)?.let { pps = it }
        if (watcher.changed(frame.data, frame.size)) {
            Log.i(TAG, "SPS nouveau ou modifié, configuration du décodeur")
            configure()
        } else if (codec == null && sps != null) {
            Log.i(TAG, "reconfiguration du décodeur depuis le SPS/PPS en cache")
            configure()
        }
        val c = codec ?: return // pas encore de SPS
        // Jamais d'image abandonnée faute de tampon : on attend (la chaîne de références doit rester intacte).
        var idx = c.dequeueInputBuffer(INPUT_TIMEOUT_US)
        while (idx < 0 && running) {
            drain()
            idx = c.dequeueInputBuffer(INPUT_TIMEOUT_US)
        }
        if (idx < 0) return
        val buf = c.getInputBuffer(idx) ?: return
        buf.clear()
        val n = minOf(frame.size, buf.capacity())
        if (n < frame.size) Log.w(TAG, "image tronquée : ${frame.size} > ${buf.capacity()}")
        buf.put(frame.data, 0, n)
        val flags = if (H264.isConfigOnly(frame.data, frame.size)) MediaCodec.BUFFER_FLAG_CODEC_CONFIG else 0
        c.queueInputBuffer(idx, 0, n, frame.ptsUs, flags)
    }

    private fun startCode(nal: ByteArray): java.nio.ByteBuffer =
        java.nio.ByteBuffer.wrap(byteArrayOf(0, 0, 0, 1) + nal)

    private fun configure() {
        resetCodec()
        val c = MediaCodec.createDecoderByType(MIME)
        try {
            val format = MediaFormat.createVideoFormat(MIME, width, height)
            val lowLatency = try {
                c.codecInfo.getCapabilitiesForType(MIME)
                    .isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)
            } catch (_: Exception) {
                false
            }
            if (lowLatency && Build.VERSION.SDK_INT >= 30) format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            if (Build.VERSION.SDK_INT >= 23) {
                format.setInteger(MediaFormat.KEY_PRIORITY, 0)
                format.setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE.toInt())
            }
            sps?.let { format.setByteBuffer("csd-0", startCode(it)) }
            pps?.let { format.setByteBuffer("csd-1", startCode(it)) }
            c.configure(format, surface, null, 0)
            c.start()
            codec = c
            firstFrameLogged = false
            Log.i(TAG, "décodeur configuré : ${c.name} ${width}x$height lowLatency=$lowLatency")
        } catch (e: Exception) {
            try { c.release() } catch (_: Exception) {}
            throw e
        }
    }

    private fun drain() {
        val c = codec ?: return
        val info = MediaCodec.BufferInfo()
        while (running) {
            val idx = c.dequeueOutputBuffer(info, 0)
            when {
                idx >= 0 -> {
                    c.releaseOutputBuffer(idx, true)
                    framesRendered++
                    if (!firstFrameLogged) {
                        firstFrameLogged = true
                        Log.i(TAG, "première image affichée")
                    }
                    if (framesRendered % 300 == 0L) Log.i(TAG, "images affichées : $framesRendered")
                }
                idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED ->
                    Log.i(TAG, "format de sortie : ${c.outputFormat}")
                idx == MediaCodec.INFO_TRY_AGAIN_LATER -> return
                else -> {}
            }
        }
    }

    /** Libère le codec ; il sera reconfiguré aussitôt avec le SPS/PPS en cache à la prochaine image. */
    private fun resetCodec() {
        val c = codec ?: return
        codec = null
        try { c.stop() } catch (_: Exception) {}
        try { c.release() } catch (_: Exception) {}
    }

    private companion object {
        const val TAG = "KairoCast"
        const val MIME = "video/avc"
        const val QUEUE_CAPACITY = 120
        const val INPUT_TIMEOUT_US = 5_000L
    }
}
