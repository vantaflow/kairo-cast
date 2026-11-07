package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Test

class SessionArbiterTest {
    private fun a(p: MirrorSource, n: MirrorSource, has: Boolean = true) = SessionArbiter.action(p, n, has)

    @Test fun airplayReopenWithoutCloseStartsFreshPipeline() =
        assertEquals(PipelineAction.START_FRESH, a(MirrorSource.AIRPLAY, MirrorSource.AIRPLAY))

    @Test fun firstOpenStartsFresh() {
        assertEquals(PipelineAction.START_FRESH, a(MirrorSource.NONE, MirrorSource.AIRPLAY, false))
        assertEquals(PipelineAction.START_FRESH, a(MirrorSource.NONE, MirrorSource.LINK))
    }

    @Test fun switchingSourceKeepsPipeline() {
        assertEquals(PipelineAction.SWITCH_SOURCE, a(MirrorSource.AIRPLAY, MirrorSource.LINK))
        assertEquals(PipelineAction.SWITCH_SOURCE, a(MirrorSource.LINK, MirrorSource.AIRPLAY))
    }

    @Test fun missingPipelineAlwaysStartsFresh() =
        assertEquals(PipelineAction.START_FRESH, a(MirrorSource.LINK, MirrorSource.AIRPLAY, false))
}
