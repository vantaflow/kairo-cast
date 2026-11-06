package com.vantaflow.kairo.cast

data class Scale(val x: Float, val y: Float)

object DisplayTransform {
    /** Facteurs à appliquer (pivot au centre) à une vue qui étire déjà la vidéo à sa taille. */
    fun scale(videoW: Int, videoH: Int, viewW: Int, viewH: Int, mode: DisplayMode): Scale {
        if (videoW <= 0 || videoH <= 0 || viewW <= 0 || viewH <= 0 || mode == DisplayMode.STRETCH) {
            return Scale(1f, 1f)
        }
        val videoRatio = videoW.toFloat() / videoH
        val viewRatio = viewW.toFloat() / viewH
        // Taille de la vidéo à l'écran si elle est ajustée à la largeur / à la hauteur de la vue.
        val widerThanView = videoRatio > viewRatio
        val fitToWidth = (mode == DisplayMode.FIT) == widerThanView
        return if (fitToWidth) {
            Scale(1f, viewRatio / videoRatio)
        } else {
            Scale(videoRatio / viewRatio, 1f)
        }
    }
}
