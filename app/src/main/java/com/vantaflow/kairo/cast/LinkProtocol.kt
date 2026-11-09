package com.vantaflow.kairo.cast

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException

/**
 * Protocole Kairo Link v1 (émetteur Windows → box), voir
 * docs/superpowers/specs/2026-11-09-emetteur-windows-design.md. Entiers gros-boutistes.
 */
object LinkProtocol {
    /** 7100 est pris par le flux de recopie AirPlay (serveur natif). */
    const val PORT = 7200
    const val SERVICE_TYPE = "_kairocast._tcp"
    const val VERSION = 1

    const val VIDEO_FORMAT = 1
    const val VIDEO = 2
    const val AUDIO_FORMAT = 3
    const val AUDIO = 4
    const val PING = 5
    const val BYE = 6

    const val REFUSE_BUSY = 1
    const val REFUSE_VERSION = 2

    const val MAX_PACKET = 8 * 1024 * 1024

    data class Hello(val version: Int, val senderName: String)

    /** Charge dans [data] (0 until [size]) ; [data] peut être plus grand et réutilisé. */
    class Packet(val type: Int, val ptsUs: Long, val data: ByteArray, val size: Int)

    /** null si ce n'est pas un émetteur Kairo Link. */
    fun readHello(input: DataInputStream): Hello? {
        val magic = ByteArray(4)
        input.readFully(magic)
        if (String(magic, Charsets.US_ASCII) != "KCST") return null
        val version = input.readUnsignedByte()
        val n = input.readUnsignedShort()
        val name = ByteArray(n).also { input.readFully(it) }
        return Hello(version, String(name, Charsets.UTF_8))
    }

    fun writeAccept(out: DataOutputStream, maxWidth: Int, maxHeight: Int, audio: Boolean) {
        out.write("KCOK".toByteArray(Charsets.US_ASCII))
        out.writeShort(maxWidth)
        out.writeShort(maxHeight)
        out.writeByte(if (audio) 1 else 0)
        out.flush()
    }

    fun writeRefuse(out: DataOutputStream, reason: Int) {
        out.write("KCNO".toByteArray(Charsets.US_ASCII))
        out.writeByte(reason)
        out.flush()
    }

    /** Paquet suivant, ou null en fin de flux propre (entre deux paquets). */
    fun readPacket(input: DataInputStream, reuse: ByteArray? = null): Packet? {
        val type = input.read()
        if (type < 0) return null
        val len = input.readInt()
        if (len < 0 || len > MAX_PACKET) throw IOException("paquet Kairo Link invalide : $len octets")
        val pts = input.readLong()
        val buf = if (reuse != null && reuse.size >= len) reuse else ByteArray(len)
        try {
            input.readFully(buf, 0, len)
        } catch (e: EOFException) {
            throw IOException("paquet Kairo Link tronqué", e)
        }
        return Packet(type, pts, buf, len)
    }

    private fun intAt(b: ByteArray, i: Int): Int =
        (b[i].toInt() and 0xFF shl 24) or (b[i + 1].toInt() and 0xFF shl 16) or
            (b[i + 2].toInt() and 0xFF shl 8) or (b[i + 3].toInt() and 0xFF)

    /** (largeur, hauteur, x, y, largeur utile, hauteur utile). */
    fun videoFormat(p: Packet): IntArray {
        if (p.size < 24) throw IOException("FORMAT_VIDEO trop court")
        return IntArray(6) { intAt(p.data, it * 4) }
    }

    /** (fréquence, canaux). */
    fun audioFormat(p: Packet): Pair<Int, Int> {
        if (p.size < 5) throw IOException("FORMAT_AUDIO trop court")
        return intAt(p.data, 0) to (p.data[4].toInt() and 0xFF)
    }
}
