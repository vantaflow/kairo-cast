package com.vantaflow.kairo.cast.link

import java.io.EOFException
import java.io.InputStream
import java.nio.ByteBuffer
import org.json.JSONObject

enum class LinkType(val code: Int) {
    HELLO(1), VIDEO(2), AUDIO(3), BYE(4), KEYREQ(5), PING(6);

    companion object {
        fun of(code: Int): LinkType? = values().firstOrNull { it.code == code }
    }
}

class LinkProtocolException(message: String, cause: Throwable? = null) : Exception(message, cause)

class LinkFrame(val type: LinkType, val flags: Int, val ptsUs: Long, val payload: ByteArray) {
    val isKey: Boolean get() = flags and 1 != 0
}

object LinkCodec {
    const val HEADER = 20
    const val MAX_PAYLOAD = 8 shl 20
    private val MAGIC = byteArrayOf('K'.code.toByte(), 'C'.code.toByte(), 'S'.code.toByte(), 'T'.code.toByte())

    fun encode(f: LinkFrame): ByteArray {
        if (f.payload.size > MAX_PAYLOAD) throw LinkProtocolException("charge trop grande ${f.payload.size}")
        val b = ByteBuffer.allocate(HEADER + f.payload.size)
        b.put(MAGIC).put(f.type.code.toByte()).put(f.flags.toByte()).putShort(0)
        b.putLong(f.ptsUs).putInt(f.payload.size).put(f.payload)
        return b.array()
    }

    /** null = fin de flux propre (avant le premier octet d'une trame). */
    fun read(input: InputStream): LinkFrame? {
        val h = ByteArray(HEADER)
        val n = readFully(input, h, allowEmpty = true)
        if (n == 0) return null
        if (!h.copyOfRange(0, 4).contentEquals(MAGIC)) throw LinkProtocolException("magic invalide")
        val bb = ByteBuffer.wrap(h)
        bb.position(4)
        val code = bb.get().toInt() and 0xFF
        val flags = bb.get().toInt() and 0xFF
        bb.short
        val pts = bb.long
        val len = bb.int.toLong() and 0xFFFFFFFFL
        val type = LinkType.of(code) ?: throw LinkProtocolException("type inconnu $code")
        if (len > MAX_PAYLOAD) throw LinkProtocolException("charge trop grande $len")
        val payload = ByteArray(len.toInt())
        if (len > 0) readFully(input, payload, allowEmpty = false)
        return LinkFrame(type, flags, pts, payload)
    }

    private fun readFully(input: InputStream, buf: ByteArray, allowEmpty: Boolean): Int {
        var off = 0
        while (off < buf.size) {
            val r = input.read(buf, off, buf.size - off)
            if (r < 0) {
                if (off == 0 && allowEmpty) return 0
                throw LinkProtocolException("flux tronque", EOFException())
            }
            off += r
        }
        return off
    }
}

data class Hello(val v: Int, val name: String, val w: Int, val h: Int, val fps: Int, val audio: String) {
    fun toBytes(): ByteArray = JSONObject()
        .put("v", v).put("name", name).put("w", w).put("h", h).put("fps", fps).put("audio", audio)
        .toString().toByteArray(Charsets.UTF_8)

    companion object {
        fun parse(bytes: ByteArray): Hello = try {
            val o = JSONObject(String(bytes, Charsets.UTF_8))
            Hello(o.getInt("v"), o.getString("name"), o.getInt("w"), o.getInt("h"), o.getInt("fps"), o.getString("audio"))
        } catch (e: org.json.JSONException) {
            throw LinkProtocolException("HELLO invalide", e)
        }
    }
}
