package com.vantaflow.kairo.cast

object DisplayTransform {
    private val IDENTITY = floatArrayOf(1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f)

    /**
     * Matrice 3x3 (ordre d'android.graphics.Matrix : scaleX, skewX, transX, skewY, scaleY, transY, 0, 0, 1)
     * à passer à TextureView.setTransform. Point de départ : l'image entière étirée sur la vue.
     * Le rectangle utile (content*) de l'image est ensuite placé selon [mode] :
     * FILL = homothétie uniforme qui couvre la vue (centré, débord rogné), STRETCH = rectangle
     * utile exactement sur la vue, FIT = homothétie uniforme qui tient dans la vue (centré).
     * Un rectangle utile nul ou hors de l'image est remplacé par l'image entière.
     */
    fun matrixValues(
        frameW: Int, frameH: Int,
        contentX: Int, contentY: Int, contentW: Int, contentH: Int,
        viewW: Int, viewH: Int, mode: DisplayMode,
    ): FloatArray {
        if (frameW <= 0 || frameH <= 0 || viewW <= 0 || viewH <= 0) return IDENTITY.copyOf()
        var cx = contentX
        var cy = contentY
        var cw = contentW
        var ch = contentH
        if (cw <= 0 || ch <= 0 || cx < 0 || cy < 0 || cx + cw > frameW || cy + ch > frameH) {
            cx = 0; cy = 0; cw = frameW; ch = frameH
        }
        val kx = viewW.toFloat() / frameW
        val ky = viewH.toFloat() / frameH
        val sx: Float
        val sy: Float
        when (mode) {
            DisplayMode.STRETCH -> {
                sx = frameW.toFloat() / cw
                sy = frameH.toFloat() / ch
            }
            else -> {
                val a = viewW.toFloat() / cw
                val b = viewH.toFloat() / ch
                val u = if (mode == DisplayMode.FILL) maxOf(a, b) else minOf(a, b)
                sx = u / kx
                sy = u / ky
            }
        }
        val centerX = (cx + cw / 2f) * kx
        val centerY = (cy + ch / 2f) * ky
        val tx = viewW / 2f - sx * centerX
        val ty = viewH / 2f - sy * centerY
        return floatArrayOf(sx, 0f, tx, 0f, sy, ty, 0f, 0f, 1f)
    }
}
