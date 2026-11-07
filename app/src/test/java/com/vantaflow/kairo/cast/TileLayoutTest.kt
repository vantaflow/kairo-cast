package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Random

class TileLayoutTest {
    private val w = 1920
    private val h = 1080
    private val r169 = 16f / 9f
    private val r1610 = 1.6f

    private fun compute(m: Map<Int, Float>, zoomed: Int? = null, sw: Int = w, sh: Int = h) =
        TileLayout.compute(m, sw, sh, zoomed)

    private fun overlap(a: TileRect, b: TileRect) =
        a.left < b.left + b.width && b.left < a.left + a.width &&
            a.top < b.top + b.height && b.top < a.top + a.height

    private fun assertValid(m: Map<Int, TileRect>, sw: Int, sh: Int) {
        m.values.forEach {
            assertTrue(it.width > 0 && it.height > 0)
            assertTrue(it.left >= 0 && it.top >= 0)
            assertTrue(it.left + it.width <= sw && it.top + it.height <= sh)
        }
        val l = m.values.toList()
        for (i in l.indices) for (j in i + 1 until l.size) assertFalse(overlap(l[i], l[j]))
    }

    @Test fun zeroActiveEmpty() = assertTrue(compute(emptyMap()).isEmpty())

    @Test fun oneSourceLetterboxedCentered() {
        val m = compute(mapOf(2 to r1610))
        assertEquals(mapOf(2 to TileRect(96, 0, 1728, 1080)), m)
    }

    @Test fun oneSourcePortraitCentered() {
        val t = compute(mapOf(0 to 498f / 1080f)).getValue(0)
        assertEquals(1080, t.height)
        assertEquals(498, t.width)
        assertEquals((1920 - 498) / 2, t.left)
        assertEquals(0, t.top)
    }

    @Test fun phonePlusMacOneRow() {
        val m = compute(mapOf(0 to 498f / 1080f, 1 to r1610))
        val a = m.getValue(0)
        val b = m.getValue(1)
        // Une seule rangée, hauteur commune, largeur totale <= écran.
        assertEquals(a.top, b.top)
        assertEquals(a.height, b.height)
        assertEquals(930, a.height)
        assertEquals(428, a.width)
        assertEquals(1488, b.width)
        assertEquals(a.left + a.width + TileLayout.GAP, b.left)
        assertTrue(a.width + b.width + TileLayout.GAP <= w)
        assertEquals((h - a.height) / 2, a.top)
        val margin = w - (a.width + b.width + TileLayout.GAP)
        assertEquals(margin / 2, a.left)
        assertValid(m, w, h)
    }

    @Test fun twoMacsOneRowBeatsTwoRows() {
        val m = compute(mapOf(0 to r1610, 1 to r1610))
        assertEquals(958, m.getValue(0).width)
        assertEquals(958, m.getValue(1).width)
        assertEquals(m.getValue(0).top, m.getValue(1).top)
        assertEquals(599, m.getValue(0).height)
        assertEquals(1, m.getValue(0).left)
        assertEquals(961, m.getValue(1).left)
        assertEquals((h - 599) / 2, m.getValue(0).top)
        assertValid(m, w, h)
    }

    @Test fun twoSixteenNineOnPortraitScreenStack() {
        // Deux sources en 16:9 sur un écran portrait : deux rangées valent mieux qu'une.
        val m = compute(mapOf(0 to r169, 1 to r169), sw = 1080, sh = 1920)
        assertTrue(m.getValue(0).top < m.getValue(1).top)
        assertEquals(m.getValue(0).left, m.getValue(1).left)
        assertValid(m, 1080, 1920)
    }

    @Test fun fourSixteenNineIsTwoByTwo() {
        val m = compute((0..3).associateWith { r169 })
        assertEquals(4, m.size)
        m.values.forEach { assertEquals(958, it.width); assertEquals(539, it.height) }
        assertEquals(m.getValue(0).top, m.getValue(1).top)
        assertEquals(m.getValue(2).top, m.getValue(3).top)
        assertTrue(m.getValue(2).top > m.getValue(0).top)
        assertEquals(m.getValue(0).left, m.getValue(2).left)
        assertValid(m, w, h)
    }

    @Test fun threePortraitsOneRow() {
        val m = compute((0..2).associateWith { 9f / 16f })
        val tops = m.values.map { it.top }.toSet()
        assertEquals(1, tops.size)
        m.values.forEach { assertEquals(1080, it.height); assertEquals(607, it.width) }
        assertTrue(m.getValue(0).left < m.getValue(1).left && m.getValue(1).left < m.getValue(2).left)
        assertValid(m, w, h)
    }

    @Test fun orderedBySlotNotInsertion() {
        val m = compute(linkedMapOf(3 to r169, 1 to r169))
        assertTrue(m.getValue(1).left < m.getValue(3).left)
    }

    @Test fun unknownRatioDefaultsTo169() {
        assertEquals(compute(mapOf(0 to r169, 1 to r169)), compute(mapOf(0 to 0f, 1 to Float.NaN)))
        assertEquals(compute(mapOf(0 to r169)), compute(mapOf(0 to -1f)))
    }

    @Test fun zoomShowsOnlyOneKeepingItsRatio() {
        val m = compute(mapOf(0 to r169, 1 to r1610), zoomed = 1)
        assertEquals(setOf(1), m.keys)
        assertEquals(compute(mapOf(1 to r1610)), m)
    }

    @Test fun zoomOnInactiveIgnored() {
        val s = mapOf(0 to r169, 1 to r1610)
        assertEquals(compute(s), compute(s, zoomed = 3))
    }

    @Test fun randomInputsStayInsideAndDisjoint() {
        val rnd = Random(42)
        repeat(500) {
            val n = 1 + rnd.nextInt(4)
            val src = HashMap<Int, Float>()
            while (src.size < n) src[rnd.nextInt(4)] = 0.3f + rnd.nextFloat() * 2.5f
            val sw = 400 + rnd.nextInt(3600)
            val sh = 300 + rnd.nextInt(2000)
            val m = compute(src, sw = sw, sh = sh)
            assertEquals(src.keys, m.keys)
            assertValid(m, sw, sh)
        }
    }

    @Test fun tileKeepsSourceRatio() {
        val m = compute(mapOf(0 to r1610, 1 to 0.5f, 2 to r169))
        val src = mapOf(0 to r1610, 1 to 0.5f, 2 to r169)
        m.forEach { (s, r) -> assertEquals(src.getValue(s).toDouble(), r.width.toDouble() / r.height, 0.02) }
    }

    @Test fun nextFocusInGrid() {
        val m = compute((0..3).associateWith { r169 })
        assertEquals(1, TileLayout.nextFocus(m, 0, 1, 0))
        assertEquals(2, TileLayout.nextFocus(m, 0, 0, 1))
        assertEquals(0, TileLayout.nextFocus(m, 0, -1, 0))
        assertEquals(0, TileLayout.nextFocus(m, 0, 0, -1))
        assertEquals(3, TileLayout.nextFocus(m, 1, 0, 1))
        assertEquals(1, TileLayout.nextFocus(m, 1, 1, 0))
    }

    @Test fun nextFocusRow() {
        val m = compute(mapOf(0 to r169, 1 to r169))
        assertEquals(1, TileLayout.nextFocus(m, 0, 1, 0))
        assertEquals(0, TileLayout.nextFocus(m, 1, -1, 0))
        assertEquals(0, TileLayout.nextFocus(m, 0, 0, 1))
    }

    @Test fun nextFocusListOverloadAndDuplicates() {
        assertEquals(1, TileLayout.nextFocus(listOf(0, 1), 0, 1, 0))
        assertEquals(listOf(0, 1), TileLayout.slotsInOrder(listOf(1, 0, 1)))
        assertEquals(5, TileLayout.nextFocus(listOf(0, 1), 5, 1, 0))
    }

    private fun fill(m: Map<Int, Float>, sw: Int = w, sh: Int = h) =
        TileLayout.compute(m, sw, sh, null, mode = MosaicMode.FILL_SCREEN)

    @Test fun fillScreenMacAndPortraitIphoneHaveNoBars() {
        val r = fill(mapOf(0 to r1610, 1 to 554f / 1200f))
        assertValid(r, w, h)
        assertEquals(1080, r.getValue(0).height)
        assertEquals(1080, r.getValue(1).height)
        assertEquals(498.0, r.getValue(1).width.toDouble(), 2.0)
        assertEquals(1420.0, r.getValue(0).width.toDouble(), 3.0)
        // contenu du Mac étiré : 1.6 contre 1420/1080 = 1.31, écart sous 25 %
        val t = r.getValue(0)
        assertTrue(Math.abs(r1610 / (t.width.toFloat() / t.height) - 1f) <= 0.25f)
    }

    @Test fun fillScreenSingleSourceCoversScreen() {
        val r = fill(mapOf(2 to r169))
        assertEquals(TileRect(0, 0, w, h), r.getValue(2))
    }

    @Test fun fillScreenRandomProperty() {
        val rnd = Random(42)
        repeat(500) {
            val n = 2 + rnd.nextInt(3)
            val m = (0 until n).associateWith { 0.4f + rnd.nextFloat() * 1.8f }
            val r = fill(m)
            assertEquals(n, r.size)
            assertValid(r, w, h)
            assertEquals(0, r.values.minOf { it.top })
            assertEquals(h, r.values.maxOf { it.top + it.height })
            val list = r.values.toList()
            for (i in list.indices) for (j in i + 1 until list.size) assertFalse(overlap(list[i], list[j]))
        }
    }
}
