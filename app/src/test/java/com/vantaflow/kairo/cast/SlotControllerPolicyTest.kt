package com.vantaflow.kairo.cast

import com.vantaflow.kairo.cast.link.Hello
import com.vantaflow.kairo.cast.link.LinkListener
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class SlotControllerPolicyTest {
    private class FakeStore : KeyValueStore {
        val map = HashMap<String, String>()
        override fun getString(k: String) = map[k]
        override fun putString(k: String, v: String) { map[k] = v }
    }

    private class FakePipeline : PipelineFeed {
        val events = ArrayList<String>()
        override var onResourceError: () -> Unit = {}
        override fun onPcmAudio(data: ByteArray, size: Int, ptsUs: Long) {}
        override fun onSourceChanged() {}
        override fun setAudioMuted(muted: Boolean) {}
        override fun onConnectionOpened() {}
        override fun onConnectionClosed() {}
        override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) { events += "format${width}x$height" }
        override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) { events += "video" }
        override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) {}
        override fun onAudioVolume(volumeDb: Float) {}
        override fun onVideoReset() { events += "reset" }
    }

    private class FakeLink(val listener: LinkListener) : LinkEndpoint {
        var closed = 0; var keyReq = 0
        override fun start() {}
        override fun stop() {}
        override fun closeClient() { closed++ }
        override fun requestKeyFrame() { keyReq++ }
    }

    private class FakeNative : SlotNative {
        val listeners = HashMap<Int, ReceiverListener>()
        override fun start(slot: Int, name: String, deviceId: String, keyFile: String, width: Int, height: Int, maxFps: Int, ports: SlotPorts, listener: ReceiverListener): Boolean {
            listeners[slot] = listener; return true
        }
        override fun stop(slot: Int) {}
        override fun setAdmit(slot: Int, allow: Boolean) {}
        override fun airplayTxt(slot: Int) = "deviceid=aa:bb:cc:dd:ee:0$slot".toByteArray()
        override fun raopTxt(slot: Int) = "am=Kairo".toByteArray()
        override fun raopServiceName(slot: Int) = "AABBCCDDEE0$slot@x"
    }

    private class FakeHost : SlotHost {
        override val native = FakeNative()
        val pipelines = HashMap<Int, FakePipeline>()
        val links = HashMap<Int, FakeLink>()
        override fun keyFile(slot: Int) = "k$slot"
        override fun pipeline(slot: Int): PipelineFeed? = pipelines[slot]
        override fun startPipeline(slot: Int, audioEnabled: Boolean, onKeyFrameNeeded: () -> Unit) { pipelines[slot] = FakePipeline() }
        override fun stopPipeline(slot: Int) { pipelines.remove(slot) }
        override fun newLink(port: Int, listener: LinkListener): LinkEndpoint = FakeLink(listener).also { links[port - 7300] = it }
        override fun advertise(slot: Int, name: String, raopName: String, port: Int, airplayTxt: Map<String, String>, raopTxt: Map<String, String>, linkPort: Int): () -> Unit = {}
    }

    private lateinit var host: FakeHost
    private lateinit var settings: CastSettings
    private val ctl = HashMap<Int, SlotController>()

    @Before fun setUp() {
        host = FakeHost()
        settings = CastSettings(FakeStore())
        settings.slotCount = 2
        SlotsState.reset(2, settings.slotNames)
    }

    private fun phase(slot: Int) = SlotsState.slots.value.first { it.slot == slot }.phase

    private fun pair(budget: Int): Pair<SlotController, SlotController> {
        val policy = SlotPolicy(2, StreamQuality.QHD, budgetCap = budget)
        val onFreed: (List<Int>) -> Unit = { f -> f.forEach { ctl[it]?.onSlotReleased() } }
        val a = SlotController(0, host, settings, policy, {}, onFreed)
        val b = SlotController(1, host, settings, policy, {}, onFreed)
        ctl[0] = a; ctl[1] = b
        a.start(); b.start()
        return a to b
    }

    private val one = SlotPolicy(1, StreamQuality.QHD).costOf(1920, 1080).toInt()

    @Test fun acceptedRuns() =
        assertEquals(SaturationAction.RUN, admissionOutcome(Admission.Accepted, MirrorSource.AIRPLAY))

    @Test fun saturatedAirPlayKeepsSession() =
        assertEquals(SaturationAction.REFUSE_KEEP_SESSION, admissionOutcome(Admission.Saturated, MirrorSource.AIRPLAY))

    @Test fun saturatedLinkClosesWithBye() =
        assertEquals(SaturationAction.REFUSE_CLOSE_LINK, admissionOutcome(Admission.Saturated, MirrorSource.LINK))

    @Test fun saturatedSlotShowsExactMessage() {
        pair(one)
        for (s in 0..1) { host.native.listeners[s]!!.onConnectionOpened(); host.native.listeners[s]!!.onVideoFormat(1920, 1080, 0, 0, 1920, 1080) }
        assertEquals(SlotPhase.SATURATED, phase(1))
        assertEquals("Trop de sources pour cette box", SlotPolicy.MESSAGE_SATURATED)
    }

    @Test fun releaseOfOtherSlotResumesSaturated() {
        pair(one)
        host.links[0]!!.listener.onLinkOpened(Hello(1, "A", 1920, 1080, 30, "pcm"))
        host.links[1]!!.listener.onLinkOpened(Hello(1, "B", 1920, 1080, 30, "pcm"))
        assertEquals(SlotPhase.SATURATED, phase(1))
        // la fermeture par BYE n'est pas encore revenue (onLinkClosed) : le budget libéré relance la session
        host.links[0]!!.listener.onLinkClosed()
        assertEquals(SlotPhase.MIRRORING, phase(1))
        assertEquals(1, host.links[1]!!.keyReq)
    }

    @Test fun releaseResumesSaturatedAirPlayOnce() {
        pair(one)
        val l0 = host.native.listeners[0]!!; val l1 = host.native.listeners[1]!!
        l0.onConnectionOpened(); l0.onVideoFormat(1920, 1080, 0, 0, 1920, 1080)
        l1.onConnectionOpened(); l1.onVideoFormat(1920, 1080, 0, 0, 1920, 1080)
        l0.onConnectionClosed()
        assertEquals(SlotPhase.MIRRORING, phase(1))
        assertEquals(1, host.pipelines[1]!!.events.count { it == "format1920x1080" })
    }

    @Test fun resourceErrorMarksSlotSaturated() {
        pair(Int.MAX_VALUE)
        for (s in 0..1) { host.native.listeners[s]!!.onConnectionOpened(); host.native.listeners[s]!!.onVideoFormat(1280, 800, 0, 0, 1280, 800) }
        host.pipelines[1]!!.onResourceError()
        assertEquals(SlotPhase.SATURATED, phase(1))
        assertEquals(SlotPhase.MIRRORING, phase(0))
        host.native.listeners[1]!!.onVideoData(ByteArray(2), 2, 0)
        assertTrue(host.pipelines[1]!!.events.none { it == "video" })
    }

    // --- MirrorPipeline : audio conditionnel (sans décodeur ni surface) ---
    private class FakeSink(val log: ArrayList<String>, val id: Int) : AudioSink {
        override fun setVolumeDb(db: Float) {}
        override fun start() { log += "start$id" }
        override fun startPcm(rate: Int, channels: Int) { log += "startPcm$id" }
        override fun feed(data: ByteArray, size: Int, ptsUs: Long) {}
        override fun feedPcm(data: ByteArray, size: Int, ptsUs: Long) { log += "feedPcm$id" }
        override fun release() { log += "release$id" }
    }

    @Test fun nonAudiblePipelineDoesNotCreateAudioPlayer() {
        var created = 0
        val log = ArrayList<String>()
        val p = MirrorPipeline(true, {}, audible = false, audioFactory = { FakeSink(log, ++created) })
        p.onPcmAudio(ByteArray(4), 4, 0)
        assertEquals(0, created)
        p.setAudible(true)
        p.onPcmAudio(ByteArray(4), 4, 0)
        assertEquals(1, created)
    }

    @Test fun audibleSwitchReleasesOldPlayer() {
        var created = 0
        val log = ArrayList<String>()
        val p = MirrorPipeline(true, {}, audible = true, audioFactory = { FakeSink(log, ++created) })
        p.onPcmAudio(ByteArray(4), 4, 0)
        assertEquals(1, created)
        p.setAudible(false)
        assertTrue(log.contains("release1"))
        p.onPcmAudio(ByteArray(4), 4, 0)
        assertEquals(1, created) // rien tant que non audible
        p.setAudible(true)
        p.onPcmAudio(ByteArray(4), 4, 0)
        assertEquals(2, created)
    }
}
