package com.vantaflow.kairo.cast

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.Socket
import java.util.Collections
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class LinkServerTest {
    private val events = Collections.synchronizedList(ArrayList<String>())
    private val closed = CountDownLatch(1)
    private var busy = false

    private val listener = object : ReceiverListener {
        override fun onConnectionOpened() { events += "open" }
        override fun onConnectionClosed() { events += "close"; closed.countDown() }
        override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) { events += "format ${width}x$height" }
        override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) { events += "video $size@$ptsUs" }
        override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) { events += "audio $size ct=$codecType" }
        override fun onAudioVolume(volumeDb: Float) {}
        override fun onVideoReset() {}
    }

    private val server = LinkServer(listener, { r, c -> events += "pcm $r/$c" }, { !busy }, { Triple(1920, 1080, true) }, port = 0, readTimeoutMs = 1_000)

    @After fun tearDown() = server.stop()

    private fun hello(out: DataOutputStream) {
        out.write("KCST".toByteArray()); out.writeByte(1)
        val n = "PC".toByteArray(); out.writeShort(n.size); out.write(n); out.flush()
    }

    private fun packet(out: DataOutputStream, type: Int, pts: Long, payload: ByteArray) {
        out.writeByte(type); out.writeInt(payload.size); out.writeLong(pts); out.write(payload)
    }

    @Test
    fun relaysSessionToListener() {
        assertTrue(server.start())
        Socket("127.0.0.1", server.localPort).use { s ->
            val out = DataOutputStream(s.getOutputStream())
            val input = DataInputStream(s.getInputStream())
            hello(out)
            val magic = ByteArray(4); input.readFully(magic)
            assertEquals("KCOK", String(magic))
            assertEquals(1920, input.readUnsignedShort()); assertEquals(1080, input.readUnsignedShort()); assertEquals(1, input.read())
            val fmt = java.io.ByteArrayOutputStream().also { b -> DataOutputStream(b).apply { intArrayOf(1280, 720, 0, 0, 1280, 720).forEach { writeInt(it) } } }.toByteArray()
            packet(out, LinkProtocol.VIDEO_FORMAT, 0, fmt)
            packet(out, LinkProtocol.VIDEO, 33, ByteArray(10))
            packet(out, LinkProtocol.AUDIO_FORMAT, 0, byteArrayOf(0, 0, 0xBB.toByte(), 0x80.toByte(), 2))
            packet(out, LinkProtocol.AUDIO, 40, ByteArray(8))
            packet(out, LinkProtocol.BYE, 0, ByteArray(0))
            out.flush()
            assertTrue(closed.await(3, TimeUnit.SECONDS))
        }
        assertEquals(listOf("open", "format 1280x720", "video 10@33", "pcm 48000/2", "audio 8 ct=${AudioPlayer.CT_PCM}", "close"), events.toList())
    }

    @Test
    fun refusesWhenBusy() {
        busy = true
        assertTrue(server.start())
        Socket("127.0.0.1", server.localPort).use { s ->
            hello(DataOutputStream(s.getOutputStream()))
            val r = DataInputStream(s.getInputStream())
            val magic = ByteArray(4); r.readFully(magic)
            assertEquals("KCNO", String(magic))
            assertEquals(LinkProtocol.REFUSE_BUSY, r.read())
        }
        assertTrue(events.isEmpty())
    }

    @Test
    fun closesSilentSession() {
        assertTrue(server.start())
        Socket("127.0.0.1", server.localPort).use { s ->
            hello(DataOutputStream(s.getOutputStream()))
            assertTrue(closed.await(4, TimeUnit.SECONDS))
        }
        assertEquals(listOf("open", "close"), events.toList())
    }

    @Test
    fun audioPlayerSupportsPcm() {
        assertTrue(AudioPlayer.supports(AudioPlayer.CT_PCM))
        assertTrue(!AudioPlayer.supports(AudioPlayer.CT_ALAC))
    }
}
