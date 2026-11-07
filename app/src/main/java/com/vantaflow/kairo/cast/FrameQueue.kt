package com.vantaflow.kairo.cast

import java.util.ArrayDeque
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

class Frame(val data: ByteArray, val size: Int, val ptsUs: Long, val keyFrame: Boolean)

/**
 * File bornée producteur/consommateur. AirPlay n'envoie une image clé qu'au début du flux :
 * on ne jette donc jamais de P-frames sans nécessité. Si la file est pleine, le producteur
 * attend au plus [offerWaitMs] ms ; ensuite on retire la plus ancienne image non clé (en dernier
 * recours la plus ancienne). La file accepte toujours la nouvelle image.
 */
class FrameQueue(private val capacity: Int, private val offerWaitMs: Long = 50) {
    private val lock = ReentrantLock()
    private val notEmpty = lock.newCondition()
    private val notFull = lock.newCondition()
    private val items = ArrayDeque<Frame>()
    private var initialDelivered = false // sous [lock] : la première image clé (IDR) est sortie

    /** Nombre d'images retirées faute de place (consultable pour les journaux). */
    @Volatile var dropped = 0L
        private set

    fun offer(frame: Frame) = lock.withLock {
        var nanos = TimeUnit.MILLISECONDS.toNanos(offerWaitMs)
        while (items.size >= capacity && nanos > 0L) nanos = notFull.awaitNanos(nanos)
        if (items.size >= capacity && !initialDelivered) {
            // Avant le premier IDR (codec en configuration, plusieurs flux qui démarrent ensemble) :
            // on ne jette jamais SPS/PPS/IDR (AirPlay ne les renvoie pas), la file peut dépasser.
            val it = items.iterator()
            while (it.hasNext()) {
                if (!it.next().keyFrame) {
                    it.remove()
                    dropped++
                    break
                }
            }
            // Plafond dur : sans IDR qui arrive, la file ne grossit pas sans limite.
            if (items.size >= capacity * HARD_CAP_FACTOR) {
                items.pollFirst()
                dropped++
            }
        } else if (items.size >= capacity) {
            val it = items.iterator()
            var removed = false
            while (it.hasNext()) {
                if (!it.next().keyFrame) {
                    it.remove()
                    removed = true
                    break
                }
            }
            if (!removed) items.pollFirst()
            dropped++
        }
        items.addLast(frame)
        notEmpty.signal()
    }

    @Throws(InterruptedException::class)
    fun poll(timeoutMs: Long): Frame? = lock.withLock {
        var nanos = TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        while (items.isEmpty()) {
            if (nanos <= 0L) return null
            nanos = notEmpty.awaitNanos(nanos)
        }
        val f = items.pollFirst()
        if (f.keyFrame && !H264.isConfigOnly(f.data, f.size)) initialDelivered = true
        notFull.signal()
        f
    }

    fun clear() = lock.withLock {
        items.clear()
        initialDelivered = false
        notFull.signalAll()
    }

    private companion object {
        const val HARD_CAP_FACTOR = 4
    }
}
