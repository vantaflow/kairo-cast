package com.vantaflow.kairo.cast.sender

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Bundle
import android.util.Log
import android.view.Surface

/**
 * Encodeur H.264 matériel alimenté par une surface (l'écran recopié). Chaque image sort en
 * Annex-B ; SPS/PPS sont recopiés devant les images clés ([AnnexB.accessUnit]).
 */
class ScreenEncoder private constructor(
    private val codec: MediaCodec,
    val width: Int,
    val height: Int,
    val surface: Surface,
) {
    @Volatile private var running = false
    private var thread: Thread? = null
    private var config: ByteArray? = null

    /** [onFrame] (unité d'accès, image clé, pts µs) et [onError] sont appelés depuis le fil de l'encodeur. */
    fun start(onFrame: (ByteArray, Boolean, Long) -> Unit, onError: (Exception) -> Unit) {
        codec.start()
        running = true
        thread = Thread({
            val info = MediaCodec.BufferInfo()
            try {
                while (running) {
                    val i = codec.dequeueOutputBuffer(info, 100_000)
                    if (i < 0) continue
                    val buf = codec.getOutputBuffer(i)
                    if (buf != null && info.size > 0) {
                        val data = ByteArray(info.size)
                        buf.position(info.offset)
                        buf.get(data, 0, info.size)
                        if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                            config = data
                        } else {
                            val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                            onFrame(AnnexB.accessUnit(config, data, key), key, info.presentationTimeUs)
                        }
                    }
                    codec.releaseOutputBuffer(i, false)
                }
            } catch (e: Exception) {
                if (running) onError(e)
            }
        }, "kairo-envoi-encodeur").apply { isDaemon = true; start() }
    }

    fun requestKeyFrame() {
        try { codec.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }) } catch (_: Exception) {}
    }

    fun release() {
        running = false
        try { thread?.join(500) } catch (_: InterruptedException) {}
        try { codec.stop() } catch (_: Exception) {}
        try { codec.release() } catch (_: Exception) {}
        try { surface.release() } catch (_: Exception) {}
    }

    companion object {
        private const val MIME = MediaFormat.MIMETYPE_VIDEO_AVC
        const val FPS = 30

        /** Premier format accepté par l'encodeur, du plus net au plus petit. */
        fun create(screenW: Int, screenH: Int): ScreenEncoder {
            var last: Exception? = null
            for (long in VideoSizing.LONG_SIDES) {
                val (w, h) = VideoSizing.fit(screenW, screenH, long)
                val codec = MediaCodec.createEncoderByType(MIME)
                try {
                    val caps = codec.codecInfo.getCapabilitiesForType(MIME).videoCapabilities
                    if (!caps.isSizeSupported(w, h)) {
                        Log.i("KairoCastEnvoi", "Encodeur ${codec.name} : ${w}x$h non pris en charge")
                        codec.release()
                        continue
                    }
                    val fmt = MediaFormat.createVideoFormat(MIME, w, h).apply {
                        setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                        setInteger(MediaFormat.KEY_BIT_RATE, VideoSizing.bitrate(w, h, FPS))
                        setInteger(MediaFormat.KEY_FRAME_RATE, FPS)
                        setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
                        setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
                        setInteger(MediaFormat.KEY_PRIORITY, 0)
                        // Écran immobile : l'encodeur répète la dernière image, la box reste alimentée.
                        setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000L)
                    }
                    codec.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                    val surface = codec.createInputSurface()
                    Log.i("KairoCastEnvoi", "Encodeur ${codec.name} : ${w}x$h")
                    return ScreenEncoder(codec, w, h, surface)
                } catch (e: Exception) {
                    last = e
                    try { codec.release() } catch (_: Exception) {}
                }
            }
            throw IllegalStateException("aucun format vidéo accepté par l'encodeur", last)
        }
    }
}
