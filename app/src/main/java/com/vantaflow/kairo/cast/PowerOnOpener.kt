package com.vantaflow.kairo.cast

import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.util.Log

/**
 * Affiche l'accueil de Kairo Cast à l'allumage de la TV (sortie de veille ou démarrage), si le réglage le demande.
 * Un court délai laisse la TV finir d'afficher sa propre source, sinon elle repasserait par-dessus.
 */
object PowerOnOpener {
    private const val DELAY_MS = 3000L
    private val main = Handler(Looper.getMainLooper())

    fun open(context: Context) {
        val app = context.applicationContext
        main.removeCallbacksAndMessages(this)
        main.postAtTime({
            if (SlotsState.active.value.isNotEmpty()) return@postAtTime // une recopie est déjà affichée
            try {
                app.startActivity(
                    Intent(app, MainActivity::class.java)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT)
                )
            } catch (e: Exception) {
                Log.w("KairoCast", "ouverture à l'allumage impossible : $e")
            }
        }, this, android.os.SystemClock.uptimeMillis() + DELAY_MS)
    }
}
