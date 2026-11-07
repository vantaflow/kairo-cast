package com.vantaflow.kairo.cast

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaCodecList
import android.media.MediaFormat
import android.util.Log
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Lecture du son AirPlay : décodage MediaCodec (AAC-ELD ou AAC-LC) sur un thread dédié, sortie
 * AudioTrack. Une instance = un type de codec. [feed] ne bloque jamais (file bornée, la plus
 * ancienne trame est abandonnée si elle est pleine). Aucune exception ne remonte à l'appelant.
 */
class AudioPlayer(private val codecType: Int) {
    private class Packet(val data: ByteArray, val ptsUs: Long)

    private val queue = ArrayBlockingQueue<Packet>(if (codecType == CT_PCM) PCM_QUEUE_CAPACITY else QUEUE_CAPACITY)
    private var pcmRate = SAMPLE_RATE
    private var pcmChannels = CHANNELS
    @Volatile private var running = false
    @Volatile private var failed = false
    @Volatile private var gain = 1f
    private var thread: Thread? = null
    private var track: AudioTrack? = null // thread audio uniquement
    private var played = 0L
    private var dropped = 0L

    val isUsable: Boolean get() = !failed

    fun start() {
        if (running || failed) return
        running = true
        thread = Thread(::run, "kairo-audio").also { it.start() }
    }

    /** Mode PCM s16le (Kairo Link) : pas de MediaCodec, lecture directe sur l'AudioTrack. */
    fun startPcm(sampleRate: Int, channels: Int) {
        if (codecType != CT_PCM) return
        pcmRate = sampleRate
        pcmChannels = channels
        start()
    }

    fun feedPcm(data: ByteArray, size: Int, ptsUs: Long = 0) {
        if (!running || failed || size <= 0) return
        enqueue(Packet(data.copyOf(size), ptsUs))
    }

    fun feed(data: ByteArray, size: Int, ptsUs: Long) {
        if (!running || failed || size <= 0) return
        if (size == 4 && data[0] == 0.toByte() && data[1] == 0x68.toByte() && data[2] == 0x34.toByte()) return // paquet vide
        enqueue(Packet(data.copyOf(size), ptsUs))
    }

    private fun enqueue(p: Packet) {
        while (!queue.offer(p)) {
            queue.poll() // latence bornée : on jette le plus ancien
            if (++dropped % 100 == 1L) Log.w(TAG, "audio : trame abandonnée (file pleine), total $dropped")
        }
    }

    fun setVolumeDb(db: Float) {
        gain = gainFor(db)
        try {
            track?.setVolume(gain)
        } catch (e: Exception) {
            Log.w(TAG, "audio : volume non appliqué : $e")
        }
    }

    fun release() {
        // Coupure immédiate : le tampon de la piste ne doit pas finir de jouer pendant l'arrêt du fil.
        try {
            track?.setVolume(0f)
            track?.pause()
            track?.flush()
        } catch (_: Exception) {}
        running = false
        thread?.interrupt()
    }

    private fun createCodec(): MediaCodec? {
        val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, SAMPLE_RATE, CHANNELS)
        when (codecType) {
            CT_AAC_ELD -> {
                format.setInteger(MediaFormat.KEY_AAC_PROFILE, PROFILE_ELD)
                format.setByteBuffer("csd-0", java.nio.ByteBuffer.wrap(ASC_ELD))
            }
            CT_AAC_LC -> {
                format.setInteger(MediaFormat.KEY_AAC_PROFILE, 2)
                format.setByteBuffer("csd-0", java.nio.ByteBuffer.wrap(ASC_LC))
            }
            else -> return null
        }
        val codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_AAC)
        try {
            codec.configure(format, null, null, 0)
            codec.start()
        } catch (e: Exception) {
            codec.release()
            throw e
        }
        Log.i(TAG, "décodeur audio configuré : ${codec.name}")
        return codec
    }

    private fun run() {
        var codec: MediaCodec? = null
        try {
            if (codecType == CT_PCM) {
                runPcm()
                return
            }
            codec = createCodec()
            if (codec == null) {
                Log.w(TAG, "audio : codec ct=$codecType non pris en charge, son désactivé")
                failed = true
                return
            }
            val info = MediaCodec.BufferInfo()
            while (running) {
                val p = queue.poll(20, TimeUnit.MILLISECONDS)
                if (p != null) queueInput(codec, p)
                drain(codec, info)
            }
        } catch (e: InterruptedException) {
            // arrêt demandé
        } catch (e: Exception) {
            Log.e(TAG, "audio : erreur, son désactivé pour la session", e)
            failed = true
        } finally {
            running = false
            queue.clear()
            try { codec?.stop() } catch (_: Exception) {}
            try { codec?.release() } catch (_: Exception) {}
            try { track?.stop() } catch (_: Exception) {}
            try { track?.release() } catch (_: Exception) {}
            track = null
        }
    }

    private fun runPcm() {
        val t = createTrack(pcmRate, pcmChannels)
        while (running) {
            val p = queue.poll(20, TimeUnit.MILLISECONDS) ?: continue
            t.write(p.data, 0, p.data.size)
            if (++played % 500 == 0L) Log.i(TAG, "audio : $played blocs PCM joués")
        }
    }

    private fun queueInput(codec: MediaCodec, p: Packet) {
        var tries = 0
        while (running) {
            val i = codec.dequeueInputBuffer(5_000)
            if (i >= 0) {
                val buf = codec.getInputBuffer(i) ?: return
                buf.clear()
                if (p.data.size > buf.remaining()) return
                buf.put(p.data)
                codec.queueInputBuffer(i, 0, p.data.size, p.ptsUs, 0)
                return
            }
            if (++tries >= 4) return // codec saturé : trame abandonnée plutôt que de s'accumuler
        }
    }

    private fun drain(codec: MediaCodec, info: MediaCodec.BufferInfo) {
        while (running) {
            val o = codec.dequeueOutputBuffer(info, 0)
            when {
                o >= 0 -> {
                    if (info.size > 0) {
                        val out = codec.getOutputBuffer(o)
                        if (out != null) {
                            val t = track ?: createTrack(codec.outputFormat)
                            val bytes = ByteArray(info.size)
                            out.position(info.offset)
                            out.get(bytes, 0, info.size)
                            t.write(bytes, 0, bytes.size)
                            if (++played % 500 == 0L) Log.i(TAG, "audio : $played trames jouées")
                        }
                    }
                    codec.releaseOutputBuffer(o, false)
                }
                o == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    track?.release()
                    track = null
                    createTrack(codec.outputFormat)
                }
                else -> return
            }
        }
    }

    private fun createTrack(f: MediaFormat): AudioTrack {
        val rate = if (f.containsKey(MediaFormat.KEY_SAMPLE_RATE)) f.getInteger(MediaFormat.KEY_SAMPLE_RATE) else SAMPLE_RATE
        val ch = if (f.containsKey(MediaFormat.KEY_CHANNEL_COUNT)) f.getInteger(MediaFormat.KEY_CHANNEL_COUNT) else CHANNELS
        return createTrack(rate, ch)
    }

    private fun createTrack(rate: Int, ch: Int): AudioTrack {
        val mask = if (ch == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val min = AudioTrack.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT)
        val t = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build()
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(rate)
                    .setChannelMask(mask)
                    .build()
            )
            .setBufferSizeInBytes(maxOf(min, 1) * 4)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
        t.setVolume(gain)
        t.play()
        track = t
        Log.i(TAG, "audio : piste ${rate} Hz, $ch canaux, tampon ${maxOf(min, 1) * 4} octets")
        return t
    }

    companion object {
        const val CT_ALAC = 2
        const val CT_AAC_LC = 4
        const val CT_AAC_ELD = 8
        /** Pseudo-codec : PCM s16le reçu par Kairo Link. */
        const val CT_PCM = 100
        private const val PCM_QUEUE_CAPACITY = 100
        private const val TAG = "KairoCast"
        private const val SAMPLE_RATE = 44100
        private const val CHANNELS = 2
        private const val PROFILE_ELD = 39
        private const val QUEUE_CAPACITY = 40
        private val ASC_ELD = byteArrayOf(0xF8.toByte(), 0xE8.toByte(), 0x50, 0x00)
        private val ASC_LC = byteArrayOf(0x12, 0x10)

        /** Volume AirPlay (dB ; -144 = muet, -30..0) vers gain linéaire ; muet si ≤ -30. */
        fun gainFor(db: Float): Float = when {
            db.isNaN() || db <= -30f -> 0f
            db >= 0f -> 1f
            else -> Math.pow(10.0, db / 20.0).toFloat()
        }

        /** Journalise (une fois) les décodeurs audio disponibles. */
        fun logAvailableDecoders() {
            try {
                val names = MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos
                    .filter { !it.isEncoder }
                    .filter { i -> i.supportedTypes.any { it.equals("audio/mp4a-latm", true) || it.equals("audio/alac", true) } }
                    .joinToString { it.name + "=" + it.supportedTypes.joinToString("/") }
                Log.i(TAG, "audio : décodeurs disponibles : $names")
            } catch (e: Exception) {
                Log.w(TAG, "audio : liste des décodeurs impossible : $e")
            }
        }
    }
}
