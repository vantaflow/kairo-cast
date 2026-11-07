package com.vantaflow.kairo.cast

import android.view.KeyEvent

sealed interface MirrorKeyAction {
    data class MoveFocus(val dx: Int, val dy: Int) : MirrorKeyAction
    object CycleMode : MirrorKeyAction
    object ToggleZoom : MirrorKeyAction
    object PinAudio : MirrorKeyAction
    object None : MirrorKeyAction
}

/** Traduction pure des touches de la télécommande pour l'écran de recopie. */
object MirrorKeys {
    fun map(keyCode: Int, repeat: Int, activeCount: Int): MirrorKeyAction {
        val several = activeCount >= 2
        return when (keyCode) {
            KeyEvent.KEYCODE_DPAD_LEFT -> if (several) MirrorKeyAction.MoveFocus(-1, 0) else MirrorKeyAction.None
            KeyEvent.KEYCODE_DPAD_RIGHT -> if (several) MirrorKeyAction.MoveFocus(1, 0) else MirrorKeyAction.None
            KeyEvent.KEYCODE_DPAD_UP -> if (several) MirrorKeyAction.MoveFocus(0, -1) else MirrorKeyAction.None
            KeyEvent.KEYCODE_DPAD_DOWN -> if (several) MirrorKeyAction.MoveFocus(0, 1) else MirrorKeyAction.None
            // OK : une seule source = change de mode d'affichage (comme avant) ; plusieurs = zoom de la tuile.
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER ->
                if (repeat != 0) MirrorKeyAction.None else if (several) MirrorKeyAction.ToggleZoom else MirrorKeyAction.CycleMode
            // Menu : plusieurs = prendre le son de la tuile ; une seule = mode d'affichage (comportement v1).
            KeyEvent.KEYCODE_MENU ->
                if (repeat != 0) MirrorKeyAction.None else if (several) MirrorKeyAction.PinAudio else MirrorKeyAction.CycleMode
            else -> MirrorKeyAction.None
        }
    }
}
