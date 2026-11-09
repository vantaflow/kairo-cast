package com.vantaflow.kairo.cast

import com.vantaflow.kairo.cast.SourceArbiter.Source
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SourceArbiterTest {
    private val a = SourceArbiter()

    @Test fun linkClaimsFreeScreen() {
        assertTrue(a.tryClaimLink())
        assertEquals(Source.LINK, a.current)
        assertFalse(a.tryClaimLink())
    }

    @Test fun linkRefusedDuringAirplay() {
        assertTrue(a.airplayOpened())
        assertFalse(a.tryClaimLink())
        assertTrue(a.accepts(Source.AIRPLAY))
        assertFalse(a.accepts(Source.LINK))
    }

    @Test fun airplayIgnoredDuringLinkThenPromoted() {
        a.tryClaimLink()
        assertFalse(a.airplayOpened())
        assertFalse(a.accepts(Source.AIRPLAY))
        assertTrue(a.linkClosed()) // le Mac est toujours connecté : il reprend la main
        assertEquals(Source.AIRPLAY, a.current)
        assertTrue(a.accepts(Source.AIRPLAY))
    }

    @Test fun airplayClosedDuringLinkIsForgotten() {
        a.tryClaimLink()
        a.airplayOpened()
        assertFalse(a.airplayClosed()) // ne coupe pas la session Link
        assertEquals(Source.LINK, a.current)
        assertFalse(a.linkClosed())
        assertEquals(Source.NONE, a.current)
    }

    @Test fun airplayCloseFreesScreen() {
        a.airplayOpened()
        assertTrue(a.airplayClosed())
        assertTrue(a.tryClaimLink())
    }

    @Test fun repeatedAirplayOpenStartsOnce() {
        assertTrue(a.airplayOpened())
        assertFalse(a.airplayOpened())
    }

    @Test fun lateLinkCloseIsIgnored() {
        a.airplayOpened()
        assertFalse(a.linkClosed())
        assertEquals(Source.AIRPLAY, a.current)
    }
}
