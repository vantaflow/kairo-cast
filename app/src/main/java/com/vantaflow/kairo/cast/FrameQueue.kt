package com.vantaflow.kairo.cast

import java.util.ArrayDeque
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

class Frame(val data: ByteArray, val size: Int, val ptsUs: Long, val keyFrame: Boolean)

/** File bornée producteur/consommateur : si elle déborde, on vide et on attend une image clé. */
class FrameQueue(private val capacity: Int) {
    private val lock = ReentrantLock()
    private val notEmpty = lock.newCondition()
    private val items = ArrayDeque<Frame>()
    private var waitingForKey = false

    fun offer(frame: Frame) = lock.withLock {
        if (waitingForKey) {
            if (!frame.keyFrame) return
            waitingForKey = false
        }
        if (items.size >= capacity) {
            items.clear()
            if (!frame.keyFrame) {
                waitingForKey = true
                return
            }
        }
        items.addLast(frame)
        notEmpty.signal()
    }

    fun poll(timeoutMs: Long): Frame? = lock.withLock {
        var nanos = TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        while (items.isEmpty()) {
            if (nanos <= 0L) return null
            nanos = notEmpty.awaitNanos(nanos)
        }
        items.pollFirst()
    }

    fun clear() = lock.withLock {
        items.clear()
        waitingForKey = false
    }
}
