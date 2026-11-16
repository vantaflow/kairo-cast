package com.vantaflow.kairo.cast.sender

import android.os.Handler
import android.os.Looper

/** État du partage, partagé entre le service et l'écran (lu et notifié sur le fil principal). */
object CastState {
    /** Source vers laquelle on partage, null hors partage. */
    var box: Box? = null
        private set
    /** Dernier message : « Connexion… », « Partage en cours… », raison d'un arrêt. */
    var message: String = ""
        private set

    private val main = Handler(Looper.getMainLooper())
    private val listeners = mutableListOf<() -> Unit>()

    fun listen(l: () -> Unit) { listeners += l }
    fun unlisten(l: () -> Unit) { listeners -= l }

    fun set(box: Box?, message: String) = main.post {
        this.box = box
        this.message = message
        listeners.toList().forEach { it() }
    }.let {}
}
