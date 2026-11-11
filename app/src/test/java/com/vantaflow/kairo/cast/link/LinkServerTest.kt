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
        override fun status() = """{"busy":true,"source":"link","by":"PC-Salon"}"""
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

    @Test fun statusAnswersWithoutTouchingTheSession() {
        connect("PC-Salon")
        assertEquals("open:PC-Salon", rec.next())
        val q = Socket("127.0.0.1", server.boundPort)
        sockets.add(q)
        send(q, LinkFrame(LinkType.STATUS, 0, 0, ByteArray(0)))
        val r = awaitFrame(q, LinkType.STATUS)
        assertNotNull(r)
        assertEquals("""{"busy":true,"source":"link","by":"PC-Salon"}""", String(r!!.payload, Charsets.UTF_8))
        assertEquals(null, rec.next(300)) // la session du PC n'est ni fermée ni remplacée
        assertTrue(server.hasClient)
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

    private fun rawHello(w: Int, h: Int) =
        LinkFrame(LinkType.HELLO, 0, 0, Hello(1, "PC", w, h, 30, "pcm_s16le_48000_2").toBytes())

    @Test fun helloWithAbsurdSizeIsRejected() {
        for ((w, h) in listOf(65535 to 65535, 4097 to 100, 0 to 100, -1 to 5)) {
            val s = Socket("127.0.0.1", server.boundPort); sockets.add(s)
            send(s, rawHello(w, h))
        }
        assertEquals(null, rec.next(500))
    }

    @Test fun oversizedFirstFrameIsRefused() {
        val s = Socket("127.0.0.1", server.boundPort); sockets.add(s)
        send(s, LinkFrame(LinkType.HELLO, 0, 0, ByteArray(100_000)))
        s.soTimeout = 2000
        // le serveur coupe sans lire la charge (fin de flux ou RST)
        val closed = try { s.getInputStream().read() == -1 } catch (_: java.io.IOException) { true }
        assertTrue(closed)
        assertEquals(null, rec.next(200))
    }

    @Test fun silentClientIsClosedAfterHelloDeadline() {
        val s = Socket("127.0.0.1", server.boundPort); sockets.add(s)
        s.soTimeout = 3000
        val t0 = System.currentTimeMillis()
        assertEquals(-1, s.getInputStream().read())
        assertTrue(System.currentTimeMillis() - t0 < 2500)
    }

    @Test fun pendingClientsAreCapped() {
        server.stop()
        rec = Rec()
        server = LinkServer(0, rec, 10_000, 5_000)
        server.start()
        val silent = (1..LinkServer.MAX_PENDING).map { Socket("127.0.0.1", server.boundPort).also { s -> sockets.add(s) } }
        Thread.sleep(200)
        val extra = Socket("127.0.0.1", server.boundPort); sockets.add(extra)
        extra.soTimeout = 1500
        assertEquals(-1, extra.getInputStream().read()) // refusée aussitôt
        assertEquals(LinkServer.MAX_PENDING, silent.size)
        // un client légitime passe dès qu'une place se libère
        silent[0].close()
        Thread.sleep(300)
        connect("OK")
        assertEquals("open:OK", rec.next())
    }
}
