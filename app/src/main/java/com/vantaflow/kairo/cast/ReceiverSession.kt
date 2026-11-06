package com.vantaflow.kairo.cast

class ReceiverSession(private val onStart: () -> Unit, private val onStop: () -> Unit) {
    private val lock = Any()
    private var open = false

    val active: Boolean get() = synchronized(lock) { open }

    fun opened() {
        synchronized(lock) {
            if (open) return
            open = true
            onStart()
        }
    }

    fun closed() {
        synchronized(lock) {
            if (!open) return
            open = false
            onStop()
        }
    }
}
