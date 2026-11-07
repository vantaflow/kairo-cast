package com.vantaflow.kairo.cast

import kotlin.math.abs

data class TileRect(val left: Int, val top: Int, val width: Int, val height: Int) {
    val centerX2: Int get() = 2 * left + width
    val centerY2: Int get() = 2 * top + height
}

/**
 * Mosaïque adaptative : fonction pure, sans état.
 *
 * Chaque tuile garde le format (largeur/hauteur) de sa source : aucune déformation, aucun recadrage.
 * Dans sa tuile, le contenu est donc affiché en FIT. [DisplayMode] ne sert qu'au cas d'une source
 * unique en plein écran.
 */
object TileLayout {
    const val GAP = 2
    const val DEFAULT_RATIO = 16f / 9f

    fun slotsInOrder(active: List<Int>): List<Int> = active.distinct().sorted()

    private class Plan(val rects: Map<Int, TileRect>, val area: Long, val rows: Int)

    fun compute(
        sources: Map<Int, Float>,
        screenW: Int,
        screenH: Int,
        zoomed: Int? = null,
        gap: Int = GAP,
    ): Map<Int, TileRect> {
        val slots = slotsInOrder(sources.keys.toList())
        if (slots.isEmpty() || screenW <= 0 || screenH <= 0) return emptyMap()
        fun ratioOf(s: Int): Float = sources[s]?.takeIf { it > 0f && it.isFinite() } ?: DEFAULT_RATIO
        val shown = if (zoomed != null && zoomed in slots) listOf(zoomed) else slots
        val ratios = shown.map { ratioOf(it) }
        var best: Plan? = null
        // Une rangée, puis deux rangées avec la coupure k = 1..n-1 (ordre des emplacements conservé).
        for (k in 0 until shown.size) {
            val rows = if (k == 0) listOf(0 until shown.size) else listOf(0 until k, k until shown.size)
            val plan = plan(shown, ratios, rows, screenW, screenH, gap)
            if (best == null || plan.area > best.area) best = plan
        }
        return best!!.rects
    }

    private fun plan(
        slots: List<Int>,
        ratios: List<Float>,
        rows: List<IntRange>,
        screenW: Int,
        screenH: Int,
        gap: Int,
    ): Plan {
        val heights = rows.map { r ->
            val sum = r.sumOf { ratios[it].toDouble() }
            (screenW - (r.count() - 1) * gap) / sum
        }
        val vGaps = (rows.size - 1) * gap
        val scale = minOf(1.0, (screenH - vGaps) / heights.sum())
        val rowH = heights.map { maxOf(1, (it * scale).toInt()) }
        val blockH = rowH.sum() + vGaps
        var top = (screenH - blockH) / 2
        val out = LinkedHashMap<Int, TileRect>()
        var area = 0L
        rows.forEachIndexed { i, r ->
            val ws = r.map { maxOf(1, (ratios[it] * rowH[i]).toInt()) }
            val rowW = ws.sum() + (r.count() - 1) * gap
            var left = (screenW - rowW) / 2
            r.forEachIndexed { j, idx ->
                out[slots[idx]] = TileRect(left, top, ws[j], rowH[i])
                area += ws[j].toLong() * rowH[i]
                left += ws[j] + gap
            }
            top += rowH[i] + gap
        }
        return Plan(out, area, rows.size)
    }

    /** Voisin le plus proche dans la direction demandée, d'après la géométrie calculée. */
    fun nextFocus(rects: Map<Int, TileRect>, current: Int, dx: Int, dy: Int): Int {
        val cur = rects[current] ?: return current
        val horizontal = dx != 0
        if (!horizontal && dy == 0) return current
        var best = current
        var bestScore = Long.MAX_VALUE
        for (s in rects.keys.sorted()) {
            if (s == current) continue
            val r = rects.getValue(s)
            val along = if (horizontal) (r.centerX2 - cur.centerX2) * dx.coerceIn(-1, 1)
            else (r.centerY2 - cur.centerY2) * dy.coerceIn(-1, 1)
            if (along <= 0) continue
            val across = if (horizontal) abs(r.centerY2 - cur.centerY2) else abs(r.centerX2 - cur.centerX2)
            val score = along.toLong() + 2L * across
            if (score < bestScore) { bestScore = score; best = s }
        }
        return best
    }

    /** Variante sans formats connus : tout en 16:9 sur un écran 1920x1080. */
    fun nextFocus(active: List<Int>, current: Int, dx: Int, dy: Int): Int {
        val slots = slotsInOrder(active)
        if (current !in slots) return current
        return nextFocus(compute(slots.associateWith { DEFAULT_RATIO }, 1920, 1080), current, dx, dy)
    }
}
