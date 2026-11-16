package com.vantaflow.kairo.cast.sender

/**
 * File d'envoi vers la box, bornée en octets. Si le Wi-Fi ne suit pas, on jette des images plutôt
 * que d'accumuler du retard : après une image perdue, plus rien jusqu'à la prochaine image clé
 * (sinon l'image se dégrade), et une image clé vide la vidéo en attente.
 */
class SendQueue(private val maxBytes: Int = 4 shl 20) {
    enum class Result { QUEUED, DROPPED, NEED_KEY }

    private class Item(val data: ByteArray, val video: Boolean)

    private val items = ArrayDeque<Item>()
    private var bytes = 0L
    private var waitingKey = false
    private var closed = false

    @Synchronized fun offerVideo(data: ByteArray, key: Boolean): Result {
        if (closed) return Result.DROPPED
        if (key) {
            if (bytes + data.size > maxBytes) dropVideo()
            waitingKey = false
            add(Item(data, true))
            return Result.QUEUED
        }
        if (waitingKey) return Result.DROPPED
        if (bytes + data.size > maxBytes) {
            waitingKey = true
            return Result.NEED_KEY
        }
        add(Item(data, true))
        return Result.QUEUED
    }

    /** Son et trames de contrôle : jetés seulement si la file est pleine. */
    @Synchronized fun offer(data: ByteArray): Boolean {
        if (closed || bytes + data.size > maxBytes) return false
        add(Item(data, false))
        return true
    }

    /** Attend la prochaine trame ; null une fois fermée. */
    @Synchronized fun take(): ByteArray? {
        while (items.isEmpty() && !closed) (this as Object).wait()
        if (closed) return null
        val it = items.removeFirst()
        bytes -= it.data.size
        return it.data
    }

    @Synchronized fun close() {
        closed = true
        items.clear()
        bytes = 0
        (this as Object).notifyAll()
    }

    @get:Synchronized val queuedBytes: Long get() = bytes

    private fun add(i: Item) {
        items.addLast(i)
        bytes += i.data.size
        (this as Object).notifyAll()
    }

    private fun dropVideo() {
        val keep = items.filter { !it.video }
        items.clear()
        items.addAll(keep)
        bytes = keep.sumOf { it.data.size.toLong() }
    }
}
