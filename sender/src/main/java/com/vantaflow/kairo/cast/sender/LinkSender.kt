package com.vantaflow.kairo.cast.sender

import android.util.Log
import com.vantaflow.kairo.cast.link.Hello
import com.vantaflow.kairo.cast.link.LinkCodec
import com.vantaflow.kairo.cast.link.LinkFrame
import com.vantaflow.kairo.cast.link.LinkType
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicBoolean

/** Raison d'une fin de session décidée côté box ou réseau ; [text] = ressource affichée, [log] = journal. */
enum class CloseReason(val text: Int, val log: String) {
    BOX_CLOSED(R.string.closed_by_box, "la box a fermé la session"),
    BOX_SILENT(R.string.closed_silent, "la box ne répond plus"),
    CONNECTION_LOST(R.string.closed_lost, "connexion avec la box perdue"),
}

/**
 * Client Kairo Link : HELLO, puis vidéo (H.264 Annex-B) et son (PCM s16le 48 kHz stéréo).
 * Un fil d'écriture vide la [SendQueue], un fil de lecture traite KEYREQ / BYE ; PING toutes les
 * [pingMs] ms ; coupure si la box ne dit plus rien pendant [timeoutMs] ms.
 */
class LinkSender(
    private val host: String,
    private val port: Int,
    private val hello: Hello,
    private val onKeyFrameRequested: () -> Unit,
    /** Appelé une seule fois : null = fermeture demandée ici, sinon la raison affichée. */
    private val onClosed: (CloseReason?) -> Unit,
    private val timeoutMs: Int = 6000,
    private val pingMs: Long = 2000,
    private val queue: SendQueue = SendQueue(),
    private val log: (String) -> Unit = { m -> try { Log.i("KairoCastEnvoi", m) } catch (_: Throwable) {} },
) {
    private val socket = Socket()
    private val finished = AtomicBoolean(false)
    private var threads = emptyList<Thread>()
    @Volatile private var bye = false

    /** Bloquant : connexion et HELLO. Lance une IOException si la box est injoignable. */
    @Throws(IOException::class)
    fun connect() {
        socket.connect(InetSocketAddress(host, port), 4000)
        socket.tcpNoDelay = true
        socket.soTimeout = timeoutMs
        socket.sendBufferSize = 1 shl 20
        val out = socket.getOutputStream()
        out.write(LinkCodec.encode(LinkFrame(LinkType.HELLO, 0, 0, hello.toBytes())))
        out.flush()
        log("Liaison : connectée à $host:$port, HELLO ${hello.w}x${hello.h}@${hello.fps}")
        threads = listOf(
            Thread(::writeLoop, "kairo-envoi-ecriture"),
            Thread(::readLoop, "kairo-envoi-lecture"),
            Thread(::pingLoop, "kairo-envoi-ping"),
        ).onEach { it.isDaemon = true; it.start() }
    }

    fun sendVideo(au: ByteArray, key: Boolean, ptsUs: Long) {
        val r = queue.offerVideo(LinkCodec.encode(LinkFrame(LinkType.VIDEO, if (key) 1 else 0, ptsUs, au)), key)
        if (r == SendQueue.Result.NEED_KEY) {
            log("Liaison : réseau trop lent, images jetées jusqu'à la prochaine image clé")
            onKeyFrameRequested()
        }
    }

    fun sendAudio(pcm: ByteArray, size: Int, ptsUs: Long) {
        queue.offer(LinkCodec.encode(LinkFrame(LinkType.AUDIO, 0, ptsUs, pcm.copyOf(size))))
    }

    /** Ferme la session : BYE au mieux (par le fil d'écriture, jamais en même temps qu'une trame), puis socket fermé. */
    fun close() {
        if (!finished.compareAndSet(false, true)) return
        bye = true
        queue.close()
        // Si le fil d'écriture est bloqué sur un réseau saturé, on coupe quand même.
        Thread({
            try { Thread.sleep(500) } catch (_: InterruptedException) {}
            try { socket.close() } catch (_: Exception) {}
        }, "kairo-envoi-fermeture").apply { isDaemon = true; start() }
        onClosed(null)
    }

    private fun fail(reason: CloseReason) {
        if (!finished.compareAndSet(false, true)) return
        log("Liaison : ${reason.log}")
        queue.close()
        try { socket.close() } catch (_: Exception) {}
        onClosed(reason)
    }

    private fun writeLoop() {
        try {
            val out = socket.getOutputStream()
            while (true) {
                val b = queue.take()
                if (b == null) {
                    if (bye) out.apply { write(LinkCodec.encode(LinkFrame(LinkType.BYE, 0, 0, ByteArray(0)))); flush() }
                    socket.close()
                    return
                }
                out.write(b)
            }
        } catch (e: Exception) {
            fail(CloseReason.CONNECTION_LOST)
        }
    }

    private fun readLoop() {
        try {
            val input = socket.getInputStream().buffered()
            while (!finished.get()) {
                val f = LinkCodec.read(input, 4096) ?: return fail(CloseReason.BOX_CLOSED)
                when (f.type) {
                    LinkType.KEYREQ -> onKeyFrameRequested()
                    LinkType.BYE -> return fail(CloseReason.BOX_CLOSED)
                    else -> {} // PING : la réception suffit à remettre le délai à zéro
                }
            }
        } catch (_: SocketTimeoutException) {
            fail(CloseReason.BOX_SILENT)
        } catch (_: Exception) {
            fail(CloseReason.CONNECTION_LOST)
        }
    }

    private fun pingLoop() {
        val ping = LinkCodec.encode(LinkFrame(LinkType.PING, 0, 0, ByteArray(0)))
        while (!finished.get()) {
            try { Thread.sleep(pingMs) } catch (_: InterruptedException) { return }
            queue.offer(ping)
        }
    }
}
