package com.vantaflow.kairo.cast

data class TileRect(val left: Int, val top: Int, val width: Int, val height: Int)

/** Disposition des tuiles : fonction pure, sans état. */
object TileLayout {
    const val GAP = 2

    fun slotsInOrder(active: List<Int>): List<Int> = active.distinct().sorted()

    fun compute(active: List<Int>, screenW: Int, screenH: Int, zoomed: Int? = null): Map<Int, TileRect> {
        val slots = slotsInOrder(active)
        val full = TileRect(0, 0, screenW, screenH)
        if (slots.isEmpty()) return emptyMap()
        if (zoomed != null && zoomed in slots) return mapOf(zoomed to full)
        if (slots.size == 1) return mapOf(slots[0] to full)
        val w0 = (screenW - GAP) / 2
        val w1 = screenW - GAP - w0
        if (slots.size == 2) {
            return mapOf(
                slots[0] to TileRect(0, 0, w0, screenH),
                slots[1] to TileRect(w0 + GAP, 0, w1, screenH),
            )
        }
        val h0 = (screenH - GAP) / 2
        val h1 = screenH - GAP - h0
        return slots.associateWith { s ->
            val col = s % 2
            val row = s / 2
            TileRect(
                if (col == 0) 0 else w0 + GAP,
                if (row == 0) 0 else h0 + GAP,
                if (col == 0) w0 else w1,
                if (row == 0) h0 else h1,
            )
        }
    }

    fun nextFocus(active: List<Int>, current: Int, dx: Int, dy: Int): Int {
        val slots = slotsInOrder(active)
        if (current !in slots || slots.size < 2) return current
        if (slots.size == 2) {
            if (dx == 0) return current
            val i = (slots.indexOf(current) + dx.coerceIn(-1, 1)).coerceIn(0, 1)
            return slots[i]
        }
        val col = current % 2 + dx.coerceIn(-1, 1)
        val row = current / 2 + dy.coerceIn(-1, 1)
        if (col !in 0..1 || row !in 0..1) return current
        val target = row * 2 + col
        return if (target in slots) target else current
    }
}
