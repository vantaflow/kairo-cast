package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Test

class DisplayTransformTest {
    private fun check(s: Scale, x: Float, y: Float) {
        assertEquals(x, s.x, 0.001f)
        assertEquals(y, s.y, 0.001f)
    }

    @Test fun fit() = check(DisplayTransform.scale(2560, 1600, 1920, 1080, DisplayMode.FIT), 0.9f, 1.0f)
    @Test fun fill() = check(DisplayTransform.scale(2560, 1600, 1920, 1080, DisplayMode.FILL), 1.0f, 1.1111f)
    @Test fun stretch() = check(DisplayTransform.scale(2560, 1600, 1920, 1080, DisplayMode.STRETCH), 1f, 1f)

    @Test fun sameRatio() {
        for (m in DisplayMode.values()) check(DisplayTransform.scale(1920, 1080, 1920, 1080, m), 1f, 1f)
    }

    @Test fun zeroDimensions() {
        for (m in DisplayMode.values()) {
            check(DisplayTransform.scale(0, 1080, 1920, 1080, m), 1f, 1f)
            check(DisplayTransform.scale(1920, 1080, 0, 0, m), 1f, 1f)
        }
    }
}
