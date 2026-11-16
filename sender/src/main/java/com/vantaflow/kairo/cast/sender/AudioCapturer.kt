package com.vantaflow.kairo.cast.sender

import android.annotation.SuppressLint
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.projection.MediaProjection

/**
 * Son joué par les applis du téléphone (AudioPlaybackCapture), en PCM s16le 48 kHz stéréo par
 * blocs de 10 ms. Les applis qui interdisent la capture (appels, certaines applis vidéo) restent muettes.
 */
class AudioCapturer(projection: MediaProjection) {
    private val record: AudioRecord
    @Volatile private var running = false
    private var thread: Thread? = null

    init {
        val config = AudioPlaybackCaptureConfiguration.Builder(projection)
            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
            .addMatchingUsage(AudioAttributes.USAGE_GAME)
            .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
            .build()
        val format = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(RATE)
            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
            .build()
        val min = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT)
        @SuppressLint("MissingPermission") // vérifiée par l'appelant
        val r = AudioRecord.Builder()
            .setAudioFormat(format)
            .setBufferSizeInBytes(maxOf(min, CHUNK * 8))
            .setAudioPlaybackCaptureConfig(config)
            .build()
        if (r.state != AudioRecord.STATE_INITIALIZED) {
            r.release()
            throw IllegalStateException("capture du son refusée")
        }
        record = r
    }

    /** [onPcm] (tampon, taille, instant de capture en µs nanoTime) est appelé depuis le fil de capture. */
    fun start(onPcm: (ByteArray, Int, Long) -> Unit) {
        record.startRecording()
        running = true
        thread = Thread({
            val buf = ByteArray(CHUNK)
            while (running) {
                val n = record.read(buf, 0, buf.size)
                if (n > 0) onPcm(buf, n, System.nanoTime() / 1000)
                else if (n < 0) break
            }
        }, "kairo-envoi-son").apply { isDaemon = true; start() }
    }

    fun release() {
        running = false
        try { record.stop() } catch (_: Exception) {}
        try { thread?.join(300) } catch (_: InterruptedException) {}
        record.release()
    }

    companion object {
        const val RATE = 48000
        const val CHUNK = RATE / 100 * 4 // 10 ms, stéréo 16 bits
        const val FORMAT = "pcm_s16le_48000_2"
    }
}
