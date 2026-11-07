package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Test

class DisplayTransformTest {
    private fun m(fw: Int, fh: Int, cx: Int, cy: Int, cw: Int, ch: Int, vw: Int, vh: Int, mode: DisplayMode) =
        DisplayTransform.matrixValues(fw, fh, cx, cy, cw, ch, vw, vh, mode)

    private fun check(v: FloatArray, sx: Float, sy: Float, tx: Float, ty: Float) {
        assertEquals(sx, v[0], 0.001f)
        assertEquals(sy, v[4], 0.001f)
        assertEquals(tx, v[2], 0.01f)
        assertEquals(ty, v[5], 0.01f)
    }

    @Test fun letterboxedStretch() =
        check(m(1920, 1080, 96, 0, 1728, 1080, 1920, 1080, DisplayMode.STRETCH), 1.1111f, 1f, -106.67f, 0f)

    @Test fun letterboxedFill() =
        check(m(1920, 1080, 96, 0, 1728, 1080, 1920, 1080, DisplayMode.FILL), 1.1111f, 1.1111f, -106.67f, -60f)

    @Test fun letterboxedFit() =
        check(m(1920, 1080, 96, 0, 1728, 1080, 1920, 1080, DisplayMode.FIT), 1f, 1f, 0f, 0f)

    @Test fun fullFrameFit() =
        check(m(2560, 1600, 0, 0, 2560, 1600, 1920, 1080, DisplayMode.FIT), 0.9f, 1f, 96f, 0f)

    @Test fun fullFrameFill() =
        check(m(2560, 1600, 0, 0, 2560, 1600, 1920, 1080, DisplayMode.FILL), 1f, 1.1111f, 0f, -60f)

    @Test fun fullFrameStretch() =
        check(m(2560, 1600, 0, 0, 2560, 1600, 1920, 1080, DisplayMode.STRETCH), 1f, 1f, 0f, 0f)

    @Test fun invalidContentUsesFullFrame() {
        for (mode in DisplayMode.values()) {
            val a = m(2560, 1600, 0, 0, 0, 0, 1920, 1080, mode)
            val b = m(2560, 1600, 0, 0, 2560, 1600, 1920, 1080, mode)
            assertEquals(b.toList(), a.toList())
        }
    }

    @Test fun sameRatioIsIdentity() {
        for (mode in DisplayMode.values()) check(m(1920, 1080, 0, 0, 1920, 1080, 1920, 1080, mode), 1f, 1f, 0f, 0f)
    }

    @Test fun zeroDimensionsIdentity() {
        for (mode in DisplayMode.values()) {
            check(m(0, 1080, 0, 0, 0, 0, 1920, 1080, mode), 1f, 1f, 0f, 0f)
            check(m(1920, 1080, 0, 0, 1920, 1080, 0, 0, mode), 1f, 1f, 0f, 0f)
        }
    }
}
