package com.vantaflow.kairo.cast

import android.view.KeyEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class MirrorKeysTest {
    @Test fun okTogglesZoomWithSeveralAndCyclesWithOne() {
        assertEquals(MirrorKeyAction.ToggleZoom, MirrorKeys.map(KeyEvent.KEYCODE_DPAD_CENTER, 0, 3))
        assertEquals(MirrorKeyAction.CycleMode, MirrorKeys.map(KeyEvent.KEYCODE_DPAD_CENTER, 0, 1))
        assertEquals(MirrorKeyAction.CycleMode, MirrorKeys.map(KeyEvent.KEYCODE_ENTER, 0, 1))
    }

    @Test fun menuPinsAudioWhenSeveral() =
        assertEquals(MirrorKeyAction.PinAudio, MirrorKeys.map(KeyEvent.KEYCODE_MENU, 0, 2))

    @Test fun menuIsCycleWhenSingle() =
        assertEquals(MirrorKeyAction.CycleMode, MirrorKeys.map(KeyEvent.KEYCODE_MENU, 0, 1))

    @Test fun dpadMovesFocusOnlyWhenSeveral() {
        assertEquals(MirrorKeyAction.MoveFocus(-1, 0), MirrorKeys.map(KeyEvent.KEYCODE_DPAD_LEFT, 0, 2))
        assertEquals(MirrorKeyAction.MoveFocus(0, 1), MirrorKeys.map(KeyEvent.KEYCODE_DPAD_DOWN, 0, 4))
        assertEquals(MirrorKeyAction.None, MirrorKeys.map(KeyEvent.KEYCODE_DPAD_LEFT, 0, 1))
    }

    @Test fun repeatedOkIgnored() {
        assertEquals(MirrorKeyAction.None, MirrorKeys.map(KeyEvent.KEYCODE_DPAD_CENTER, 1, 1))
        assertEquals(MirrorKeyAction.None, MirrorKeys.map(KeyEvent.KEYCODE_MENU, 2, 3))
    }

    @Test fun backIsNone() =
        assertEquals(MirrorKeyAction.None, MirrorKeys.map(KeyEvent.KEYCODE_BACK, 0, 3))

    @Test fun labelVisibleFourSecondsThenHidden() {
        var t = 1000L
        val timer = TileLabelTimer(4000) { t }
        assertFalse(timer.visible(0))
        timer.touch(0)
        t = 5499; assertTrue(timer.visible(0))
        t = 5500; assertFalse(timer.visible(0))
        timer.touch(0); timer.forget(0)
        assertFalse(timer.visible(0))
    }

    @Test fun labelFadesOutBeforeEnd() {
        var t = 0L
        val timer = TileLabelTimer(4000, 500) { t }
        timer.touch(1)
        t = 4000; assertEquals(1f, timer.alpha(1), 0.001f) // 4 s pleinement opaque
        t = 4250; assertEquals(0.5f, timer.alpha(1), 0.001f)
        t = 4500; assertEquals(0f, timer.alpha(1), 0.001f)
    }
}
