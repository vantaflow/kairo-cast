package com.vantaflow.kairo.cast.link

import java.net.Socket
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class LinkServerTest {
    private class Rec : LinkListener {
        val events = LinkedBlockingQueue<String>()
        val videos = LinkedBlockingQueue<Triple<ByteArray, Long, Boolean>>()
        val audios = LinkedBlockingQueue<ByteArray>()
        val hellos = CopyOnWriteArrayList<Hello>()
        override fun onLinkOpened(hello: Hello) { hellos.add(hello); events.add("open:${hello.name}") }
        override fun onLinkVideo(data: ByteArray, size: Int, ptsUs: Long, key: Boolean) { videos.add(Triple(data.copyOf(size), ptsUs, key)) }
        override fun onLinkAudio(pcm: ByteArray, size: Int, ptsUs: Long) { audios.add(pcm.copyOf(size)) }
        override fun onLinkClosed() { events.add("closed") }
        fun next(ms: Long = 2000): String? = events.poll(ms, TimeUnit.MILLISECONDS)
    }

    private lateinit var rec: Rec
    private lateinit var server: LinkServer
    private val sockets = ArrayList<Socket>()

    @Before fun setUp() {
        rec = Rec()
        server = LinkServer(0, rec, 300)
        server.start()
    }

    @After fun tearDown() {
        server.stop()
        sockets.forEach { try { it.close() } catch (_: Exception) {} }
    }

    private fun connect(name: String = "PC"): Socket {
        val s = Socket("127.0.0.1", server.boundPort)
        sockets.add(s)
        send(s, LinkFrame(LinkType.HELLO, 0, 0, Hello(1, name, 1920, 1080, 30, "pcm_s16le_48000_2").toBytes()))
        return s
    }

    private fun send(s: Socket, f: LinkFrame) { s.getOutputStream().write(LinkCodec.encode(f)); s.getOutputStream().flush() }

    /** Lit des trames jusqu'à trouver [type] (les PING du serveur sont ignorés). */
    private fun awaitFrame(s: Socket, type: LinkType): LinkFrame? {
        s.soTimeout = 2000
        try {
            while (true) {
                val f = LinkCodec.read(s.getInputStream()) ?: return null
                if (f.type == type) return f
            }
        } catch (_: Exception) { return null }
    }

    @Test fun helloOpensSession() {
        connect("PC-Salon")
        assertEquals("open:PC-Salon", rec.next())
        assertEquals(1920, rec.hellos[0].w)
    }

    @Test fun videoIsDeliveredWithKeyFlag() {
        val s = connect()
        rec.next()
        send(s, LinkFrame(LinkType.VIDEO, 1, 123456, byteArrayOf(0, 0, 0, 1, 0x67)))
        send(s, LinkFrame(LinkType.VIDEO, 0, 200000, byteArrayOf(0, 0, 0, 1, 0x41)))
        val a = rec.videos.poll(2, TimeUnit.SECONDS)!!
        val b = rec.videos.poll(2, TimeUnit.SECONDS)!!
        assertTrue(a.third); assertEquals(123456L, a.second)
        assertFalse(b.third)
        assertArrayEquals(byteArrayOf(0, 0, 0, 1, 0x41), b.first)
    }

    @Test fun audioIsDelivered() {
        val s = connect()
        rec.next()
        send(s, LinkFrame(LinkType.AUDIO, 0, 5, ByteArray(1920) { 1 }))
        assertEquals(1920, rec.audios.poll(2, TimeUnit.SECONDS)!!.size)
    }

    @Test fun silentClientTimesOut() {
        connect()
        assertEquals("open:PC", rec.next())
        val t0 = System.nanoTime()
        assertEquals("closed", rec.next(2000))
        assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - t0) < 1500)
    }

    @Test fun pingsKeepSessionAlive() {
        val s = connect()
        rec.next()
        repeat(8) { send(s, LinkFrame(LinkType.PING, 0, 0, ByteArray(0))); Thread.sleep(100) }
        assertEquals(null, rec.next(10))
    }

    @Test fun secondClientReplacesFirst() {
        val first = connect("A")
        assertEquals("open:A", rec.next())
        connect("B")
        assertEquals("closed", rec.next())
        assertEquals("open:B", rec.next())
        assertNotNull(awaitFrame(first, LinkType.BYE))
    }

    @Test fun requestKeyFrameReachesClient() {
        val s = connect()
        rec.next()
        server.requestKeyFrame()
        assertNotNull(awaitFrame(s, LinkType.KEYREQ))
    }

    @Test fun byeClosesSession() {
        val s = connect()
        rec.next()
        send(s, LinkFrame(LinkType.BYE, 0, 0, ByteArray(0)))
        assertEquals("closed", rec.next())
    }

    @Test fun badMagicClosesWithoutCrash() {
        val s = connect()
        rec.next()
        s.getOutputStream().write(ByteArray(28) { 0x42 })
        assertEquals("closed", rec.next())
        assertTrue(server.isRunning)
        connect("C")
        assertEquals("open:C", rec.next())
    }

    @Test fun garbageBeforeHelloIsIgnored() {
        val s = Socket("127.0.0.1", server.boundPort); sockets.add(s)
        s.getOutputStream().write(ByteArray(28) { 0x42 })
        assertEquals(null, rec.next(300))
        connect("D")
        assertEquals("open:D", rec.next())
    }

    @Test fun stopClosesClientAndReportsClosed() {
        connect()
        rec.next()
        server.stop()
        assertEquals("closed", rec.next())
    }
}
