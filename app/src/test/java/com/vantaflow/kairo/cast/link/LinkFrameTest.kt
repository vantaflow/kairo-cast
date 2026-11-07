package com.vantaflow.kairo.cast.link

import java.io.ByteArrayInputStream
import java.io.File
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class LinkFrameTest {
    private fun vec(name: String) = File("../protocol/vectors/$name").readBytes()

    private fun read(b: ByteArray) = LinkCodec.read(ByteArrayInputStream(b))

    @Test fun roundTripValidVectors() {
        for (n in listOf("hello", "video_key", "audio", "ping", "keyreq", "bye")) {
            val b = vec("$n.bin")
            assertArrayEquals(n, b, LinkCodec.encode(read(b)!!))
        }
    }

    @Test fun videoKeyFields() {
        val f = read(vec("video_key.bin"))!!
        assertEquals(LinkType.VIDEO, f.type)
        assertEquals(1, f.flags)
        assertEquals(123456L, f.ptsUs)
        assertArrayEquals(byteArrayOf(0, 0, 0, 1, 0x67, 0x42, 0, 0x1f), f.payload)
    }

    @Test fun audioFields() {
        val f = read(vec("audio.bin"))!!
        assertEquals(LinkType.AUDIO, f.type)
        assertEquals(1000L, f.ptsUs)
        assertEquals(1920, f.payload.size)
    }

    @Test fun helloParse() {
        val h = Hello.parse(read(vec("hello.bin"))!!.payload)
        assertEquals(Hello(1, "PC-Salon", 1920, 1080, 30, "pcm_s16le_48000_2"), h)
        assertEquals(h, Hello.parse(h.toBytes()))
    }

    @Test fun emptyStreamIsNull() {
        assertNull(read(ByteArray(0)))
    }

    @Test fun invalidInputsThrow() {
        for (n in listOf("badmagic", "huge", "truncated")) {
            try {
                read(vec("$n.bin"))
                fail(n)
            } catch (e: LinkProtocolException) {
                assertTrue(true)
            }
        }
    }

    @Test fun truncatedHeaderThrows() {
        try {
            read(vec("video_key.bin").copyOf(7))
            fail()
        } catch (e: LinkProtocolException) {
        }
    }

    @Test fun unknownTypeThrows() {
        val b = vec("ping.bin").also { it[4] = 99 }
        try {
            read(b)
            fail()
        } catch (e: LinkProtocolException) {
        }
    }
}
