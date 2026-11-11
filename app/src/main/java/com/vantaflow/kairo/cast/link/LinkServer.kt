package com.vantaflow.kairo.cast.link

import android.util.Log
import com.vantaflow.kairo.cast.SlotPolicy
import java.io.IOException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/** Rappels de [LinkServer], appelés depuis les threads du serveur (jamais le thread principal). */
interface LinkListener {
    fun onLinkOpened(hello: Hello)
    fun onLinkVideo(data: ByteArray, size: Int, ptsUs: Long, key: Boolean)
    fun onLinkAudio(pcm: ByteArray, size: Int, ptsUs: Long)
    fun onLinkClosed()
    /** Réponse à STATUS : {"busy":bool,"source":"airplay"|"link"|"","by":nom de l'émetteur}. */
    fun status(): String = "{}"
}

/**
 * Serveur Kairo Link (TCP) : un thread d'acceptation, un thread de lecture par client, un seul
 * client actif (le dernier HELLO valide l'emporte). Les écritures (KEYREQ, PING, BYE) passent par
 * un thread dédié et sont sérialisées : aucun appelant ne bloque sur un socket bloqué.
 * Coupure si rien n'est reçu pendant [timeoutMs] ; PING envoyé toutes les timeoutMs / 3.
 */
class LinkServer(
    private val port: Int = DEFAULT_PORT,
    private val listener: LinkListener,
    private val timeoutMs: Long = 6000,
    private val helloTimeoutMs: Long = 3000,
    private val log: (String) -> Unit = { m -> try { Log.i(TAG, m) } catch (_: Throwable) {} },
) {
    private val lock = Any()
    private val dispatchLock = Any()
    private var server: ServerSocket? = null
    private var acceptThread: Thread? = null
    private var current: Client? = null
    private val writer = Executors.newSingleThreadScheduledExecutor { Thread(it, "kairo-link-writer").apply { isDaemon = true } }
    @Volatile private var running = false
    private val pending = java.util.concurrent.atomic.AtomicInteger(0)

    val isRunning: Boolean get() = running

    /** Port réellement écouté (utile avec le port 0). */
    val boundPort: Int get() = server?.localPort ?: -1

    @Throws(IOException::class)
    fun start() {
        synchronized(lock) {
            if (running) return
            val ss = ServerSocket()
            try {
                ss.reuseAddress = true
                ss.bind(InetSocketAddress(port))
            } catch (e: IOException) {
                try { ss.close() } catch (_: Exception) {}
                throw e
            }
            server = ss
            running = true
            acceptThread = Thread({ acceptLoop(ss) }, "kairo-link-accept").apply { isDaemon = true; start() }
        }
        val period = maxOf(timeoutMs / 3, 50)
        try {
            writer.scheduleAtFixedRate({ current?.send(LinkFrame(LinkType.PING, 0, 0, ByteArray(0))) }, period, period, TimeUnit.MILLISECONDS)
        } catch (_: java.util.concurrent.RejectedExecutionException) {
            // arrêté pendant le démarrage : rien à planifier
        }
        log("Link : serveur à l'écoute sur le port $boundPort")
    }

    fun stop() {
        val c: Client?
        synchronized(lock) {
            if (!running) return
            running = false
            try { server?.close() } catch (_: Exception) {}
            server = null
            c = current
            current = null
        }
        c?.close(bye = true)
        writer.shutdown()
    }

    /** Demande une image clé au PC si une session est active (sans bloquer l'appelant). */
    fun requestKeyFrame() {
        current?.send(LinkFrame(LinkType.KEYREQ, 0, 0, ByteArray(0)))
    }

    /** Ferme la session active en envoyant BYE ; onLinkClosed est appelé. */
    fun closeClient() {
        val c = synchronized(lock) { current.also { current = null } }
        c?.close(bye = true)
    }

    val hasClient: Boolean get() = current != null

    private fun acceptLoop(ss: ServerSocket) {
        while (running) {
            val s = try { ss.accept() } catch (e: Exception) {
                if (running) log("Link : accept échoué : $e")
                if (!running || ss.isClosed) return
                continue
            }
            try {
                s.tcpNoDelay = true
                s.soTimeout = timeoutMs.toInt()
            } catch (_: Exception) {}
            if (pending.incrementAndGet() > MAX_PENDING) {
                pending.decrementAndGet()
                log("Link : trop de connexions sans HELLO, refusée")
                try { s.close() } catch (_: Exception) {}
                continue
            }
            Thread({ Client(s).run() }, "kairo-link-client").apply { isDaemon = true; start() }
        }
    }

    private inner class Client(private val socket: Socket) {
        private val sendLock = ReentrantLock()
        private val finished = AtomicBoolean(false)
        @Volatile private var opened = false
        @Volatile private var helloReceived = false
        private val pendingReleased = AtomicBoolean(false)

        private fun releasePending() { if (pendingReleased.compareAndSet(false, true)) pending.decrementAndGet() }

        fun run() {
            val peer = socket.remoteSocketAddress
            try {
                val input = socket.getInputStream().buffered(64 * 1024)
                // Avant HELLO : première trame limitée et délai global court (un pair sans HELLO ne garde ni mémoire ni fil).
                val deadline = Thread({
                    try { Thread.sleep(helloTimeoutMs) } catch (_: InterruptedException) { return@Thread }
                    if (!helloReceived) { log("Link : pas de HELLO en ${helloTimeoutMs} ms ($peer)"); finish() }
                }, "kairo-link-hello").apply { isDaemon = true; start() }
                val first = try { LinkCodec.read(input, MAX_HELLO_PAYLOAD) } finally { helloReceived = true; deadline.interrupt() }
                if (first?.type == LinkType.STATUS) {
                    // Simple question d'état : réponse puis fermeture, la session en cours n'est pas touchée.
                    val body = try { listener.status() } catch (e: Exception) { "{}" }
                    socket.getOutputStream().write(LinkCodec.encode(LinkFrame(LinkType.STATUS, 0, 0, body.toByteArray(Charsets.UTF_8))))
                    socket.getOutputStream().flush()
                    return
                }
                if (first == null || first.type != LinkType.HELLO) throw LinkProtocolException("HELLO attendu")
                val hello = Hello.parse(first.payload)
                if (hello.v != 1 || !SlotPolicy.isValidSize(hello.w, hello.h)) throw LinkProtocolException("HELLO non pris en charge")
                releasePending()
                if (!adopt()) return
                log("Link : session ouverte par ${hello.name} ($peer) ${hello.w}x${hello.h}@${hello.fps}")
                synchronized(dispatchLock) {
                    if (finished.get()) return
                    opened = true
                    listener.onLinkOpened(hello)
                }
                while (!finished.get()) {
                    val f = LinkCodec.read(input) ?: break
                    when (f.type) {
                        LinkType.VIDEO -> dispatch { listener.onLinkVideo(f.payload, f.payload.size, f.ptsUs, f.isKey) }
                        LinkType.AUDIO -> dispatch { listener.onLinkAudio(f.payload, f.payload.size, f.ptsUs) }
                        LinkType.BYE -> break
                        else -> {} // PING / HELLO tardif : la réception remet le délai à zéro
                    }
                }
                if (!finished.get()) log("Link : client déconnecté ($peer)")
            } catch (e: java.net.SocketTimeoutException) {
                log("Link : plus rien reçu depuis ${timeoutMs} ms, session fermée")
            } catch (e: LinkProtocolException) {
                log("Link : trame invalide ($peer) : ${e.message}")
            } catch (e: IOException) {
                if (!finished.get()) log("Link : erreur réseau ($peer) : $e")
            } catch (e: Exception) {
                log("Link : erreur inattendue ($peer) : $e")
            } finally {
                releasePending()
                finish()
                synchronized(lock) { if (current === this) current = null }
            }
        }

        /** Devient le client actif ; l'ancien est fermé (BYE) et notifié avant l'ouverture du nouveau. */
        private fun adopt(): Boolean {
            val old: Client?
            synchronized(lock) {
                if (!running) return false
                old = current
                current = this
            }
            if (old != null) {
                log("Link : nouveau client, l'ancien est fermé")
                old.close(bye = true)
            }
            return true
        }

        private inline fun dispatch(block: () -> Unit) {
            synchronized(dispatchLock) {
                if (finished.get() || current !== this) return
                block()
            }
        }

        fun send(f: LinkFrame) {
            if (finished.get()) return
            try { writer.execute {
                if (finished.get()) return@execute
                try {
                    sendLock.withLock {
                        socket.getOutputStream().write(LinkCodec.encode(f))
                        socket.getOutputStream().flush()
                    }
                } catch (e: Exception) {
                    log("Link : écriture impossible : $e")
                    finish()
                }
            } } catch (_: java.util.concurrent.RejectedExecutionException) {}
        }

        /** Notifie la fermeture (une fois), puis coupe le socket après un BYE au mieux. */
        fun close(bye: Boolean) {
            val wasFinished = finished.get()
            notifyClosed()
            if (bye && !wasFinished) {
                // thread à part : le thread d'écriture peut être bloqué sur ce même socket
                Thread({
                    try {
                        if (sendLock.tryLock(300, TimeUnit.MILLISECONDS)) {
                            try {
                                socket.getOutputStream().write(LinkCodec.encode(LinkFrame(LinkType.BYE, 0, 0, ByteArray(0))))
                                socket.getOutputStream().flush()
                            } finally { sendLock.unlock() }
                        }
                    } catch (_: Exception) {}
                    closeSocket()
                }, "kairo-link-bye").apply { isDaemon = true; start() }
            } else {
                closeSocket()
            }
        }

        private fun notifyClosed() {
            synchronized(dispatchLock) {
                if (finished.compareAndSet(false, true) && opened) {
                    try { listener.onLinkClosed() } catch (e: Exception) { log("Link : onLinkClosed a échoué : $e") }
                }
            }
        }

        fun finish() {
            notifyClosed()
            closeSocket()
        }

        private fun closeSocket() {
            try { socket.close() } catch (_: Exception) {}
        }
    }

    companion object {
        const val DEFAULT_PORT = 7300
        const val MAX_PENDING = 2
        const val MAX_HELLO_PAYLOAD = 4096
        private const val TAG = "KairoCast"
    }
}
