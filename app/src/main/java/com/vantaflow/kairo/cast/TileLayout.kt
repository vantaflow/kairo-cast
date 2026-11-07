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

    private class Plan(val rects: Map<Int, TileRect>, val area: Long, val rows: Int, val rowRanges: List<IntRange> = emptyList())

    fun compute(
        sources: Map<Int, Float>,
        screenW: Int,
        screenH: Int,
        zoomed: Int? = null,
        gap: Int = GAP,
        mode: MosaicMode = MosaicMode.EXACT_FORMATS,
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
        // Remplir l'écran : même partition en rangées, mais les rangées couvrent toute la hauteur.
        if (mode == MosaicMode.FILL_SCREEN) return fillPlan(shown, ratios, best!!.rowRanges, screenW, screenH, gap)
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
        return Plan(out, area, rows.size, rows)
    }

    /**
     * Rangées à hauteur partagée selon leur hauteur naturelle ; dans une rangée, les sources
     * en portrait (rapport < 1) gardent leur largeur naturelle, les autres se partagent le reste
     * proportionnellement à leur rapport. Aucune barre en haut ni en bas, aucun trou.
     */
    private fun fillPlan(
        slots: List<Int>,
        ratios: List<Float>,
        rows: List<IntRange>,
        screenW: Int,
        screenH: Int,
        gap: Int,
    ): Map<Int, TileRect> {
        val nat = rows.map { r -> (screenW - (r.count() - 1) * gap) / r.sumOf { ratios[it].toDouble() } }
        val vGaps = (rows.size - 1) * gap
        val avail = (screenH - vGaps).coerceAtLeast(rows.size)
        val rowH = IntArray(rows.size)
        var used = 0
        rows.indices.forEach { i ->
            rowH[i] = if (i == rows.size - 1) avail - used else maxOf(1, (avail * nat[i] / nat.sum()).toInt())
            used += rowH[i]
        }
        val out = LinkedHashMap<Int, TileRect>()
        var top = 0
        rows.forEachIndexed { i, r ->
            val idx = r.toList()
            val rowAvail = screenW - (idx.size - 1) * gap
            val portrait = idx.filter { ratios[it] < 1f }
            val land = idx.filter { ratios[it] >= 1f }
            val ws = HashMap<Int, Int>()
            val portraitW = portrait.sumOf { maxOf(1, (ratios[it] * rowH[i]).toInt()) }
            if (land.isNotEmpty() && portraitW < rowAvail) {
                portrait.forEach { ws[it] = maxOf(1, (ratios[it] * rowH[i]).toInt()) }
                val rest = rowAvail - portraitW
                val sum = land.sumOf { ratios[it].toDouble() }
                var left = rest
                land.forEachIndexed { j, it ->
                    val w = if (j == land.size - 1) left else maxOf(1, (rest * ratios[it] / sum).toInt())
                    ws[it] = w; left -= w
                }
            } else if (land.isEmpty() && portraitW <= rowAvail) {
                portrait.forEach { ws[it] = maxOf(1, (ratios[it] * rowH[i]).toInt()) }
            } else {
                // Trop large : partage proportionnel au rapport sur toute la largeur.
                val sum = idx.sumOf { ratios[it].toDouble() }
                var left = rowAvail
                idx.forEachIndexed { j, it ->
                    val w = if (j == idx.size - 1) left else maxOf(1, (rowAvail * ratios[it] / sum).toInt())
                    ws[it] = w; left -= w
                }
            }
            val rowW = idx.sumOf { ws.getValue(it) } + (idx.size - 1) * gap
            var left = (screenW - rowW) / 2
            idx.forEach {
                out[slots[it]] = TileRect(left, top, ws.getValue(it), rowH[i])
                left += ws.getValue(it) + gap
            }
            top += rowH[i] + gap
        }
        return out
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
