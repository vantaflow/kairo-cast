package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TileLayoutTest {
    private val w = 1920
    private val h = 1080
    private fun compute(active: List<Int>, zoomed: Int? = null, sw: Int = w, sh: Int = h) =
        TileLayout.compute(active, sw, sh, zoomed)

    private fun overlap(a: TileRect, b: TileRect) =
        a.left < b.left + b.width && b.left < a.left + a.width &&
            a.top < b.top + b.height && b.top < a.top + a.height

    @Test fun zeroActiveEmpty() = assertTrue(compute(emptyList()).isEmpty())

    @Test fun oneActiveFullScreenWhateverTheSlot() {
        assertEquals(mapOf(2 to TileRect(0, 0, 1920, 1080)), compute(listOf(2)))
    }

    @Test fun twoSideBySide() {
        val m = compute(listOf(0, 1))
        val a = m.getValue(0)
        val b = m.getValue(1)
        assertEquals(0, a.left)
        assertEquals(961, b.left)
        assertEquals(1080, a.height)
        assertEquals(1080, b.height)
        assertEquals(959, a.width)
        assertEquals(959, b.width)
        assertEquals(a.left + a.width + TileLayout.GAP, b.left)
        assertFalse(overlap(a, b))
    }

    @Test fun twoOrderedBySlotNotArrival() {
        val m = compute(listOf(3, 1))
        assertTrue(m.getValue(1).left < m.getValue(3).left)
    }

    @Test fun threeIsGrid() {
        val m = compute(listOf(0, 1, 3))
        assertEquals(setOf(0, 1, 3), m.keys)
        val br = m.getValue(3)
        assertTrue(br.left > 0 && br.top > 0)
        assertEquals(m.getValue(1).left, br.left)
        assertEquals(m.getValue(0).left, 0)
        assertEquals(m.getValue(0).top, 0)
    }

    @Test fun fourGrid() {
        val m = compute(listOf(0, 1, 2, 3))
        assertEquals(4, m.size)
        m.values.forEach { assertEquals(959, it.width); assertEquals(539, it.height) }
        val l = m.values.toList()
        for (i in l.indices) for (j in i + 1 until l.size) assertFalse(overlap(l[i], l[j]))
    }

    @Test fun tilesInsideScreen() {
        for (n in 1..4) for (sw in listOf(1920, 3840)) {
            val sh = sw * 9 / 16
            compute((0 until n).toList(), sw = sw, sh = sh).values.forEach {
                assertTrue(it.left >= 0 && it.top >= 0)
                assertTrue(it.left + it.width <= sw && it.top + it.height <= sh)
            }
        }
    }

    @Test fun zoomShowsOnlyOne() {
        assertEquals(mapOf(1 to TileRect(0, 0, w, h)), compute(listOf(0, 1), zoomed = 1))
    }

    @Test fun zoomOnInactiveIgnored() {
        assertEquals(compute(listOf(0, 1)), compute(listOf(0, 1), zoomed = 3))
    }

    @Test fun duplicatesIgnored() {
        assertEquals(compute(listOf(0, 1)), compute(listOf(1, 0, 1, 0)))
        assertEquals(listOf(0, 1), TileLayout.slotsInOrder(listOf(1, 0, 1)))
    }

    @Test fun otherScreenSize() {
        val m = compute(listOf(0, 1, 2, 3), sw = 3840, sh = 2160)
        m.values.forEach { assertEquals(1919, it.width); assertEquals(1079, it.height) }
        assertEquals(1921, m.getValue(1).left)
        assertEquals(1081, m.getValue(2).top)
    }

    @Test fun nextFocusMovesInGrid() {
        val a = listOf(0, 1, 2, 3)
        assertEquals(1, TileLayout.nextFocus(a, 0, 1, 0))
        assertEquals(2, TileLayout.nextFocus(a, 0, 0, 1))
        assertEquals(0, TileLayout.nextFocus(a, 0, -1, 0))
        assertEquals(0, TileLayout.nextFocus(a, 0, 0, -1))
        assertEquals(1, TileLayout.nextFocus(a, 1, 1, 0))
    }

    @Test fun nextFocusTwoSideBySide() {
        assertEquals(1, TileLayout.nextFocus(listOf(0, 1), 0, 1, 0))
        assertEquals(0, TileLayout.nextFocus(listOf(0, 1), 1, -1, 0))
        assertEquals(0, TileLayout.nextFocus(listOf(0, 1), 0, 0, 1))
    }

    @Test fun nextFocusSkipsEmptyCell() {
        val a = listOf(0, 1, 3)
        assertEquals(3, TileLayout.nextFocus(a, 1, 0, 1))
        assertEquals(0, TileLayout.nextFocus(a, 0, 0, 1))
    }
}
