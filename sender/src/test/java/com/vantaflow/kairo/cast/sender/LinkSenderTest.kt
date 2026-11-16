package com.vantaflow.kairo.cast.sender

import com.vantaflow.kairo.cast.link.Hello
import com.vantaflow.kairo.cast.link.LinkCodec
import com.vantaflow.kairo.cast.link.LinkFrame
import com.vantaflow.kairo.cast.link.LinkType
import java.net.ServerSocket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LinkSenderTest {
    private val hello = Hello(1, "Pixel", 864, 1920, 30, AudioCapturer.FORMAT)

    @Test fun helloVideoAudioThenBye() {
        ServerSocket(0).use { ss ->
            val frames = LinkedBlockingQueue<LinkFrame>()
            val server = Thread {
                ss.accept().use { s ->
                    val input = s.getInputStream()
                    while (true) { val f = LinkCodec.read(input) ?: break; frames.add(f); if (f.type == LinkType.BYE) break }
                }
            }.apply { start() }
            val closed = LinkedBlockingQueue<String>()
            val l = LinkSender("127.0.0.1", ss.localPort, hello, {}, { closed.add(it ?: "ici") }, log = {})
            l.connect()
            l.sendVideo(byteArrayOf(0, 0, 0, 1, 0x65), true, 1000)
            l.sendAudio(ByteArray(1920) { 7 }, 1920, 2000)
            Thread.sleep(100)
            l.close()
            server.join(2000)
            val h = frames.poll(1, TimeUnit.SECONDS)!!
            assertEquals(LinkType.HELLO, h.type)
            assertEquals(hello, Hello.parse(h.payload))
            val v = frames.poll(1, TimeUnit.SECONDS)!!
            assertEquals(LinkType.VIDEO, v.type)
            assertTrue(v.isKey)
            assertEquals(1000L, v.ptsUs)
            val a = frames.poll(1, TimeUnit.SECONDS)!!
            assertEquals(LinkType.AUDIO, a.type)
            assertArrayEquals(ByteArray(1920) { 7 }, a.payload)
            assertEquals(LinkType.BYE, frames.poll(1, TimeUnit.SECONDS)!!.type)
            assertEquals("ici", closed.poll(1, TimeUnit.SECONDS))
        }
    }

    @Test fun keyRequestAndByeFromTheBox() {
        ServerSocket(0).use { ss ->
            val keyReq = CountDownLatch(1)
            val closed = LinkedBlockingQueue<String>()
            val server = Thread {
                ss.accept().use { s ->
                    LinkCodec.read(s.getInputStream()) // HELLO
                    s.getOutputStream().write(LinkCodec.encode(LinkFrame(LinkType.KEYREQ, 0, 0, ByteArray(0))))
                    Thread.sleep(100)
                    s.getOutputStream().write(LinkCodec.encode(LinkFrame(LinkType.BYE, 0, 0, ByteArray(0))))
                    Thread.sleep(200)
                }
            }.apply { start() }
            val l = LinkSender("127.0.0.1", ss.localPort, hello, { keyReq.countDown() }, { closed.add(it ?: "ici") }, log = {})
            l.connect()
            assertTrue(keyReq.await(2, TimeUnit.SECONDS))
            assertEquals("La box a fermé la session", closed.poll(2, TimeUnit.SECONDS))
            server.join(2000)
        }
    }

    @Test fun silentBoxTimesOut() {
        ServerSocket(0).use { ss ->
            val closed = LinkedBlockingQueue<String>()
            val server = Thread { ss.accept().use { Thread.sleep(1500) } }.apply { start() }
            val l = LinkSender("127.0.0.1", ss.localPort, hello, {}, { closed.add(it ?: "ici") }, timeoutMs = 300, pingMs = 100, log = {})
            l.connect()
            assertEquals("La box ne répond plus", closed.poll(2, TimeUnit.SECONDS))
            server.join(3000)
        }
    }

    @Test fun statusQuery() {
        ServerSocket(0).use { ss ->
            val server = Thread {
                ss.accept().use { s ->
                    val f = LinkCodec.read(s.getInputStream())!!
                    assertEquals(LinkType.STATUS, f.type)
                    s.getOutputStream().write(LinkCodec.encode(LinkFrame(LinkType.STATUS, 0, 0, """{"busy":true,"source":"link","by":"PC"}""".toByteArray())))
                }
            }.apply { start() }
            val st = BoxStatus.query("127.0.0.1", ss.localPort)
            assertNotNull(st)
            assertEquals("occupée par PC", st!!.describe())
            server.join(2000)
        }
    }

    @Test fun oldBoxClosesOnStatus() {
        ServerSocket(0).use { ss ->
            val server = Thread { ss.accept().use { LinkCodec.read(it.getInputStream()) } }.apply { start() }
            assertEquals(null, BoxStatus.query("127.0.0.1", ss.localPort))
            server.join(2000)
        }
    }
}
