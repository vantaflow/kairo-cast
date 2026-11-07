package com.vantaflow.kairo.cast

import com.vantaflow.kairo.cast.link.Hello
import com.vantaflow.kairo.cast.link.LinkListener
import java.util.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class SlotControllerTest {
    private class FakeStore : KeyValueStore {
        val map = HashMap<String, String>()
        override fun getString(k: String) = map[k]
        override fun putString(k: String, v: String) { map[k] = v }
    }

    private class FakePipeline : PipelineFeed {
        val events = ArrayList<String>()
        var muted = false
        override var onResourceError: () -> Unit = {}
        override fun onPcmAudio(data: ByteArray, size: Int, ptsUs: Long) { events += "pcm" }
        override fun onSourceChanged() { events += "switch" }
        override fun setAudioMuted(muted: Boolean) { this.muted = muted }
        override fun onConnectionOpened() {}
        override fun onConnectionClosed() {}
        override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) { events += "format${width}x$height" }
        override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) { events += "video" }
        override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) { events += "audio" }
        override fun onAudioVolume(volumeDb: Float) {}
        override fun onVideoReset() { events += "reset" }
    }

    private class FakeLink(val port: Int, val listener: LinkListener, val failOnStart: Boolean) : LinkEndpoint {
        var started = false; var stopped = false; var closed = 0; var keyReq = 0
        override fun start() { if (failOnStart) throw java.io.IOException("port pris"); started = true }
        override fun stop() { stopped = true }
        override fun closeClient() { closed++ }
        override fun requestKeyFrame() { keyReq++ }
    }

    private class FakeNative : SlotNative {
        val calls = ArrayList<String>()
        val listeners = HashMap<Int, ReceiverListener>()
        val sizes = HashMap<Int, Pair<Int, Int>>()
        var failSlot = -1
        override fun start(slot: Int, name: String, deviceId: String, keyFile: String, width: Int, height: Int, maxFps: Int, ports: SlotPorts, listener: ReceiverListener): Boolean {
            calls += "start$slot:$name:${width}x$height:fps$maxFps"
            if (slot == failSlot) return false
            listeners[slot] = listener; sizes[slot] = width to height
            return true
        }
        override fun stop(slot: Int) { calls += "stop$slot" }
        override fun setAdmit(slot: Int, allow: Boolean) { calls += "admit$slot:$allow" }
        override fun airplayTxt(slot: Int) = "deviceid=aa:bb:cc:dd:ee:0$slot".toByteArray()
        override fun raopTxt(slot: Int) = "am=Kairo".toByteArray()
        override fun raopServiceName(slot: Int) = "AABBCCDDEE0$slot@x"
    }

    private class FakeHost : SlotHost {
        override val native = FakeNative()
        val pipelines = HashMap<Int, FakePipeline>()
        val links = HashMap<Int, FakeLink>()
        val adverts = ArrayList<String>()
        var linkFails = HashSet<Int>()
        override fun keyFile(slot: Int) = "k$slot"
        override fun pipeline(slot: Int): PipelineFeed? = pipelines[slot]
        override fun startPipeline(slot: Int, audioEnabled: Boolean, onKeyFrameNeeded: () -> Unit) { pipelines[slot] = FakePipeline() }
        override fun stopPipeline(slot: Int) { pipelines.remove(slot) }
        override fun newLink(port: Int, listener: LinkListener): LinkEndpoint {
            val slot = port - 7300
            return FakeLink(port, listener, slot in linkFails).also { links[slot] = it }
        }
        override fun advertise(slot: Int, name: String, raopName: String, port: Int, airplayTxt: Map<String, String>, raopTxt: Map<String, String>, linkPort: Int): () -> Unit {
            adverts += "+$slot:$name:$raopName:$port:link$linkPort"
            return { adverts += "-$slot" }
        }
    }

    private lateinit var host: FakeHost
    private lateinit var settings: CastSettings
    private var changes = 0
    private val freed = ArrayList<List<Int>>()

    @Before fun setUp() {
        host = FakeHost()
        settings = CastSettings(FakeStore(), Random(3))
    }

    private fun controller(slot: Int, count: Int, reset: Boolean = true): SlotController {
        settings.slotCount = count
        if (reset) SlotsState.reset(count, settings.slotNames)
        return SlotController(slot, host, settings, policyFor(count), { changes++ }, { freed += it })
    }

    private val policies = HashMap<Int, SlotPolicy>()
    private fun policyFor(count: Int) = policies.getOrPut(count) { SlotPolicy(count, settings.quality) }

    private fun phase(slot: Int) = SlotsState.slots.value.first { it.slot == slot }.phase

    @Test fun startSetsAdmitBeforeNativeStartAndAdvertises() {
        val c = controller(1, 2)
        assertTrue(c.start())
        assertEquals(listOf("admit1:true", "start1:Kairo 2:1920x1200:fps30"), host.native.calls)
        assertEquals(listOf("+1:Kairo 2:AABBCCDDEE01@x:7010:link7301"), host.adverts)
        assertEquals(SlotPhase.WAITING, phase(1))
    }

    @Test fun singleSlotKeepsLegacyAnnouncement() {
        val c = controller(0, 1)
        c.start()
        assertEquals("start0:Kairo:2560x1600:fps0", host.native.calls[1])
    }

    @Test fun nativeFailureIsIsolatedError() {
        host.native.failSlot = 1
        val c1 = controller(1, 2)
        assertFalse(c1.start())
        assertEquals(SlotPhase.ERROR, phase(1))
        val c0 = controller(0, 2, reset = false)
        assertTrue(c0.start())
        assertEquals(SlotPhase.WAITING, phase(0))
        assertEquals(SlotPhase.ERROR, phase(1))
    }

    @Test fun linkPortTakenOnExtraSlotIsErrorAndStopsNative() {
        host.linkFails += 1
        val c = controller(1, 2)
        assertFalse(c.start())
        assertEquals(SlotPhase.ERROR, phase(1))
        assertTrue(host.native.calls.contains("stop1"))
        assertEquals(emptyList<String>(), host.adverts)
    }

    @Test fun linkPortTakenOnSlot0KeepsAirplayWithoutLink() {
        host.linkFails += 0
        val c = controller(0, 1)
        assertTrue(c.start())
        assertEquals("+0:Kairo:AABBCCDDEE00@x:7000:link0", host.adverts[0])
    }

    @Test fun airplayMirroringThenCloseGoesWaiting() {
        val c = controller(0, 2); c.start()
        val l = host.native.listeners[0]!!
        l.onConnectionOpened()
        l.onVideoFormat(1920, 1200, 0, 0, 1920, 1200)
        assertEquals(SlotPhase.MIRRORING, phase(0))
        l.onVideoData(ByteArray(4), 4, 0)
        assertEquals(listOf("format1920x1200", "video"), host.pipelines[0]!!.events)
        l.onConnectionClosed()
        assertEquals(SlotPhase.WAITING, phase(0))
        assertEquals(null, host.pipelines[0])
    }

    @Test fun linkTakesOverAirplayAndMacWaitsAfterwards() {
        val c = controller(0, 1); c.start()
        val l = host.native.listeners[0]!!
        l.onConnectionOpened()
        l.onVideoFormat(1440, 900, 0, 0, 1440, 900)
        val link = host.links[0]!!
        link.listener.onLinkOpened(Hello(1, "PC", 1920, 1080, 30, "pcm"))
        link.listener.onLinkVideo(ByteArray(2), 2, 0, true)
        l.onVideoData(ByteArray(2), 2, 0) // ignoré tant que Link est actif
        assertEquals(1, host.pipelines[0]!!.events.count { it == "video" })
        link.listener.onLinkClosed()
        assertEquals(SlotPhase.MAC_WAITING, phase(0))
        l.onConnectionClosed()
        assertEquals(SlotPhase.WAITING, phase(0))
    }

    @Test fun airplayArrivalClosesLinkSession() {
        val c = controller(0, 1); c.start()
        val link = host.links[0]!!
        link.listener.onLinkOpened(Hello(1, "PC", 1920, 1080, 30, "pcm"))
        host.native.listeners[0]!!.onConnectionOpened()
        assertEquals(1, link.closed)
        assertTrue(host.pipelines[0]!!.events.contains("switch"))
    }

    @Test fun saturationRefusesExtraSourceThenResumesWhenFreed() {
        // 3 sources 1280x800 : 3 x 1280x800 = 3 x 120000 ; budget plafonné à 2 flux pour le test
        val policy = SlotPolicy(4, StreamQuality.QHD, budgetCap = 2 * 1280 * 800 / 256 * 30)
        settings.slotCount = 4
        SlotsState.reset(4, settings.slotNames)
        val cs = (0..2).map { SlotController(it, host, settings, policy, { changes++ }, { f -> freed += f; f.forEach { s -> cont[s]?.resumeFromSaturation() } }) }
        cs.forEachIndexed { i, c -> cont[i] = c; c.start() }
        for (s in 0..2) {
            val l = host.native.listeners[s]!!
            l.onConnectionOpened()
            l.onVideoFormat(1280, 800, 0, 0, 1280, 800)
        }
        assertEquals(SlotPhase.MIRRORING, phase(0))
        assertEquals(SlotPhase.MIRRORING, phase(1))
        assertEquals(SlotPhase.SATURATED, phase(2))
        host.native.listeners[2]!!.onVideoData(ByteArray(2), 2, 0)
        assertEquals(listOf<String>(), host.pipelines[2]!!.events) // rien n'est décodé
        host.native.listeners[0]!!.onConnectionClosed()
        assertEquals(SlotPhase.MIRRORING, phase(2)) // reprise automatique
        assertEquals(listOf("format1280x800"), host.pipelines[2]!!.events)
    }
    private val cont = HashMap<Int, SlotController>()

    @Test fun saturatedLinkSessionIsClosed() {
        val policy = SlotPolicy(2, StreamQuality.QHD, budgetCap = 1)
        settings.slotCount = 2
        SlotsState.reset(2, settings.slotNames)
        val c0 = SlotController(0, host, settings, policy, {}, {}); c0.start()
        val c1 = SlotController(1, host, settings, policy, {}, {}); c1.start()
        host.links[0]!!.listener.onLinkOpened(Hello(1, "PC", 1920, 1080, 30, "pcm"))
        host.links[1]!!.listener.onLinkOpened(Hello(1, "PC2", 1920, 1080, 30, "pcm"))
        assertEquals(SlotPhase.SATURATED, phase(1))
        Thread.sleep(100) // la fermeture part sur un thread dédié
        assertEquals(1, host.links[1]!!.closed)
        assertEquals(0, host.links[0]!!.closed)
    }

    @Test fun stopUnadvertisesAndStopsNativeThenIgnoresCallbacks() {
        val c = controller(0, 2); c.start()
        val l = host.native.listeners[0]!!
        c.stop()
        assertEquals(listOf("-0"), host.adverts.filter { it.startsWith("-") })
        assertTrue(host.native.calls.contains("stop0"))
        assertEquals(SlotPhase.STOPPED, phase(0))
        l.onConnectionOpened() // accepting = false : ignoré
        assertEquals(null, host.pipelines[0])
    }

    @Test fun admitIsSetAgainOnRestart() {
        val c = controller(0, 1)
        c.start(); c.stop(); c.start()
        assertEquals(2, host.native.calls.count { it == "admit0:true" })
    }

    @Test fun muteAppliesToPipelineAndSurvivesNewSession() {
        val c = controller(1, 2); c.start()
        c.setAudioMuted(true)
        val l = host.native.listeners[1]!!
        l.onConnectionOpened()
        assertTrue(host.pipelines[1]!!.muted)
    }
}
