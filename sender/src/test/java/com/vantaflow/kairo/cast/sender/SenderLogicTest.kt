package com.vantaflow.kairo.cast.sender

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class SenderLogicTest {
    @Test fun phonePortraitFitsIn1920x1080() {
        val (w, h) = VideoSizing.fit(1080, 2400, 1920)
        assertEquals(1920, h)
        assertEquals(864, w)
    }

    @Test fun landscapeKeepsAspectAndMultipleOf16() {
        val (w, h) = VideoSizing.fit(2400, 1080, 1920)
        assertEquals(1920, w)
        assertEquals(864, h)
        val (w2, h2) = VideoSizing.fit(2560, 1600, 1920) // tablette 16:10 : limitée par 1080
        assertEquals(0, w2 % 16); assertEquals(0, h2 % 16)
        assertTrue(h2 <= 1080 && w2 <= 1920)
    }

    @Test fun smallScreenIsNotEnlarged() {
        assertEquals(720 to 1280, VideoSizing.fit(720, 1280, 1920))
    }

    @Test fun fallbackSidesGetSmaller() {
        val (w, h) = VideoSizing.fit(1080, 2400, 1280)
        assertEquals(1280, h); assertEquals(576, w)
    }

    @Test fun bitrateBounds() {
        assertEquals(2_000_000, VideoSizing.bitrate(320, 240))
        assertEquals(10_000_000, VideoSizing.bitrate(3840, 2160))
    }

    private val sps = byteArrayOf(0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 2)
    private val idr = byteArrayOf(0, 0, 0, 1, 0x65, 9)
    private val p = byteArrayOf(0, 0, 0, 1, 0x41, 9)

    @Test fun configGoesBeforeKeyFramesOnly() {
        assertArrayEquals(sps + idr, AnnexB.accessUnit(sps, idr, true))
        assertSame(p, AnnexB.accessUnit(sps, p, false))
        assertSame(idr, AnnexB.accessUnit(null, idr, true))
        val already = sps + idr
        assertSame(already, AnnexB.accessUnit(sps, already, true))
    }

    @Test fun spsDetection() {
        assertTrue(AnnexB.startsWithSps(sps))
        assertTrue(AnnexB.startsWithSps(byteArrayOf(0, 0, 1, 0x67, 1)))
        assertFalse(AnnexB.startsWithSps(idr))
        assertFalse(AnnexB.startsWithSps(byteArrayOf(1, 2)))
    }

    @Test fun statusParseAndDescribe() {
        assertEquals(Occupancy.Free, BoxStatus.parse("""{"busy":false,"source":"","by":""}""".toByteArray())!!.occupancy())
        assertEquals(Occupancy.ByDevice("PC-SALLE"), BoxStatus.parse("""{"busy":true,"source":"link","by":"PC-SALLE"}""".toByteArray())!!.occupancy())
        assertEquals(Occupancy.ByApple, BoxStatus.parse("""{"busy":true,"source":"airplay","by":""}""".toByteArray())!!.occupancy())
        assertEquals(Occupancy.Busy, BoxStatus.parse("""{"busy":true}""".toByteArray())!!.occupancy())
        assertEquals(Occupancy.Busy, BoxStatus.parse("""{"busy":true,"source":"link","by":""}""".toByteArray())!!.occupancy())
        assertNull(BoxStatus.parse("{}".toByteArray()))
        assertNull(BoxStatus.parse("pas du json".toByteArray()))
        assertNull(BoxStatus.parse("""{"busy":"oui"}""".toByteArray()))
    }

    @Test fun displayName() {
        assertEquals("Salle de réunion", boxDisplayName("Salle de réunion Link", null))
        assertEquals("TV", boxDisplayName("X Link", " TV "))
        assertEquals("Salle", boxDisplayName("Salle Link", ""))
    }
}
