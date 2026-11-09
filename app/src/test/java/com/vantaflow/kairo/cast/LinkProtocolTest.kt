package com.vantaflow.kairo.cast

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream

class LinkProtocolTest {
    private fun bytes(block: DataOutputStream.() -> Unit): DataInputStream {
        val out = ByteArrayOutputStream()
        DataOutputStream(out).block()
        return DataInputStream(ByteArrayInputStream(out.toByteArray()))
    }

    @Test
    fun readsHello() {
        val input = bytes {
            write("KCST".toByteArray()); writeByte(1)
            val n = "PC-Salon".toByteArray(Charsets.UTF_8); writeShort(n.size); write(n)
        }
        assertEquals(LinkProtocol.Hello(1, "PC-Salon"), LinkProtocol.readHello(input))
    }

    @Test
    fun rejectsBadMagic() {
        val input = bytes { write("HTTP".toByteArray()); writeByte(1); writeShort(0) }
        assertNull(LinkProtocol.readHello(input))
    }

    @Test
    fun writesAcceptAndRefuse() {
        val ok = ByteArrayOutputStream()
        LinkProtocol.writeAccept(DataOutputStream(ok), 2560, 1440, true)
        val r = DataInputStream(ByteArrayInputStream(ok.toByteArray()))
        val magic = ByteArray(4); r.readFully(magic)
        assertEquals("KCOK", String(magic))
        assertEquals(2560, r.readUnsignedShort()); assertEquals(1440, r.readUnsignedShort()); assertEquals(1, r.read())

        val no = ByteArrayOutputStream()
        LinkProtocol.writeRefuse(DataOutputStream(no), LinkProtocol.REFUSE_BUSY)
        assertArrayEquals("KCNO".toByteArray() + byteArrayOf(1), no.toByteArray())
    }

    @Test
    fun readsPackets() {
        val input = bytes {
            writeByte(LinkProtocol.VIDEO_FORMAT); writeInt(24); writeLong(0)
            intArrayOf(1920, 1080, 0, 0, 1920, 1080).forEach { writeInt(it) }
            writeByte(LinkProtocol.VIDEO); writeInt(3); writeLong(16_666); write(byteArrayOf(9, 8, 7))
        }
        val p1 = LinkProtocol.readPacket(input)!!
        assertEquals(LinkProtocol.VIDEO_FORMAT, p1.type)
        assertArrayEquals(intArrayOf(1920, 1080, 0, 0, 1920, 1080), LinkProtocol.videoFormat(p1))
        val p2 = LinkProtocol.readPacket(input)!!
        assertEquals(LinkProtocol.VIDEO, p2.type)
        assertEquals(16_666L, p2.ptsUs)
        assertArrayEquals(byteArrayOf(9, 8, 7), p2.data.copyOf(p2.size))
        assertNull(LinkProtocol.readPacket(input)) // fin de flux
    }

    @Test
    fun readsAudioFormat() {
        val input = bytes { writeByte(LinkProtocol.AUDIO_FORMAT); writeInt(5); writeLong(0); writeInt(48_000); writeByte(2) }
        assertEquals(48_000 to 2, LinkProtocol.audioFormat(LinkProtocol.readPacket(input)!!))
    }

    @Test(expected = java.io.IOException::class)
    fun rejectsOversizedPacket() {
        LinkProtocol.readPacket(bytes { writeByte(LinkProtocol.VIDEO); writeInt(LinkProtocol.MAX_PACKET + 1); writeLong(0) })
    }

    @Test
    fun reusesBufferWhenLargeEnough() {
        val input = bytes {
            writeByte(LinkProtocol.AUDIO); writeInt(4); writeLong(0); write(byteArrayOf(1, 2, 3, 4))
            writeByte(LinkProtocol.AUDIO); writeInt(2); writeLong(0); write(byteArrayOf(5, 6))
        }
        val a = LinkProtocol.readPacket(input)!!
        val b = LinkProtocol.readPacket(input, a.data)!!
        assertTrue(a.data === b.data)
        assertEquals(2, b.size)
    }
}
