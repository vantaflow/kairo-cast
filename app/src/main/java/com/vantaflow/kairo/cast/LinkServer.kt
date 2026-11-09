package com.vantaflow.kairo.cast

import android.util.Log
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException

/**
 * Serveur Kairo Link : reçoit la recopie d'un PC Windows et la transmet au même [ReceiverListener]
 * que le serveur AirPlay. Une session à la fois ; les autres émetteurs reçoivent « occupé ».
 */
class LinkServer(
    private val listener: ReceiverListener,
    private val onPcmFormat: (rate: Int, channels: Int) -> Unit,
    /** Réserve l'écran pour cette session ; faux si une recopie (AirPlay ou Link) est en cours. */
    private val claim: () -> Boolean,
    /** (largeur max, hauteur max, son activé), lu à chaque connexion. */
    private val limits: () -> Triple<Int, Int, Boolean>,
    private val port: Int = LinkProtocol.PORT,
    private val readTimeoutMs: Int = 5_000,
) {
    @Volatile private var server: ServerSocket? = null
    @Volatile private var current: Socket? = null
    @Volatile private var sessionThread: Thread? = null
    private var thread: Thread? = null

    /** Port réellement écouté (utile si [port] vaut 0), -1 si arrêté. */
    val localPort: Int get() = server?.localPort ?: -1

    @Synchronized
    fun start(): Boolean {
        if (server != null) return true
        val s = try {
            ServerSocket().apply {
                reuseAddress = true
                bind(InetSocketAddress(port))
            }
        } catch (e: IOException) {
            log("écoute impossible sur le port $port : $e")
            return false
        }
        server = s
        thread = Thread({ acceptLoop(s) }, "kairo-link").also { it.start() }
        return true
    }

    @Synchronized
    fun stop() {
        val s = server ?: return
        server = null
        try { s.close() } catch (_: IOException) {}
        try { current?.close() } catch (_: IOException) {}
        thread?.join(2_000)
        thread = null
        // La session se termine (rappel de fermeture compris) avant qu'un redémarrage n'en accepte une autre.
        sessionThread?.join(2_000)
        sessionThread = null
    }

    private fun acceptLoop(s: ServerSocket) {
        while (!s.isClosed) {
            val c = try { s.accept() } catch (e: IOException) { break }
            if (current != null) {
                Thread({ refuse(c) }, "kairo-link-refus").start()
                continue
            }
            current = c
            sessionThread = Thread({
                try {
                    session(c)
                } catch (e: Exception) {
                    log("session : erreur $e")
                } finally {
                    current = null
                    try { c.close() } catch (_: IOException) {}
                }
            }, "kairo-link-session").also { it.start() }
        }
    }

    private fun refuse(c: Socket) {
        try {
            c.soTimeout = 2_000
            val input = DataInputStream(BufferedInputStream(c.getInputStream()))
            if (LinkProtocol.readHello(input) != null) {
                LinkProtocol.writeRefuse(DataOutputStream(c.getOutputStream()), LinkProtocol.REFUSE_BUSY)
            }
        } catch (_: IOException) {
        } finally {
            try { c.close() } catch (_: IOException) {}
        }
    }

    private fun session(c: Socket) {
        c.tcpNoDelay = true
        c.soTimeout = readTimeoutMs
        val input = DataInputStream(BufferedInputStream(c.getInputStream(), 256 * 1024))
        val out = DataOutputStream(BufferedOutputStream(c.getOutputStream()))
        val hello = try { LinkProtocol.readHello(input) } catch (e: IOException) { null } ?: return
        if (hello.version != LinkProtocol.VERSION) {
            LinkProtocol.writeRefuse(out, LinkProtocol.REFUSE_VERSION)
            return
        }
        if (!claim()) {
            LinkProtocol.writeRefuse(out, LinkProtocol.REFUSE_BUSY)
            return
        }
        listener.onConnectionOpened()
        var buf = ByteArray(512 * 1024)
        try {
            val (w, h, audio) = limits()
            LinkProtocol.writeAccept(out, w, h, audio)
            log("session ouverte : ${hello.senderName} (${c.inetAddress.hostAddress})")
            while (true) {
                val p = LinkProtocol.readPacket(input, buf) ?: break
                if (p.data.size > buf.size) buf = p.data
                when (p.type) {
                    LinkProtocol.VIDEO_FORMAT -> {
                        val f = LinkProtocol.videoFormat(p)
                        listener.onVideoFormat(f[0], f[1], f[2], f[3], f[4], f[5])
                    }
                    LinkProtocol.VIDEO -> listener.onVideoData(p.data, p.size, p.ptsUs)
                    LinkProtocol.AUDIO_FORMAT -> {
                        val (rate, ch) = LinkProtocol.audioFormat(p)
                        onPcmFormat(rate, ch)
                    }
                    LinkProtocol.AUDIO -> listener.onAudioData(p.data, p.size, p.ptsUs, AudioPlayer.CT_PCM)
                    LinkProtocol.BYE -> break
                    else -> {} // PING et types inconnus : ignorés
                }
            }
        } catch (e: SocketTimeoutException) {
            log("session : plus de nouvelles de l'émetteur depuis ${readTimeoutMs / 1000} s")
        } catch (e: IOException) {
            log("session interrompue : $e")
        } finally {
            listener.onConnectionClosed()
            log("session fermée : ${hello.senderName}")
        }
    }

    private fun log(msg: String) {
        try { Log.i(TAG, "Kairo Link : $msg") } catch (_: RuntimeException) { println("Kairo Link : $msg") }
    }

    private companion object {
        const val TAG = "KairoCast"
    }
}
