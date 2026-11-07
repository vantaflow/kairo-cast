package com.vantaflow.kairo.cast

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertThrows
import org.junit.Test

/** Invariants Kotlin qui alimentent le serveur natif (aucune bibliothèque chargée). */
class NativeReceiverSlotsTest {
    @Test fun portsPassedToNativeComeFromSlotPorts() {
        assertArrayEquals(intArrayOf(7000, 7100, 7011, 6001, 6000), NativeReceiver.nativePortsArray(SlotPorts.of(0)))
        assertArrayEquals(intArrayOf(7010, 7110, 7021, 6011, 6010), NativeReceiver.nativePortsArray(SlotPorts.of(1)))
        for (i in 0 until MAX_SLOTS) {
            val p = SlotPorts.of(i)
            assertArrayEquals(
                intArrayOf(p.airplayHttp, p.airplayMirror, p.udpTiming, p.udpControl, p.udpData),
                NativeReceiver.nativePortsArray(p),
            )
        }
    }

    @Test fun slotOutOfRangeRejected() {
        for (i in 0 until MAX_SLOTS) NativeReceiver.requireSlot(i)
        assertThrows(IllegalArgumentException::class.java) { NativeReceiver.requireSlot(MAX_SLOTS) }
        assertThrows(IllegalArgumentException::class.java) { NativeReceiver.requireSlot(-1) }
        // le garde passe avant tout appel JNI : la bibliothèque n'est jamais chargée ici
        assertThrows(IllegalArgumentException::class.java) {
            NativeReceiver.start(4, "Kairo", "aa:bb:cc:dd:ee:ff", "/x", 1920, 1080, 0, SlotPorts.of(0), NoopListener)
        }
        assertThrows(IllegalArgumentException::class.java) { NativeReceiver.stop(4) }
        assertThrows(IllegalArgumentException::class.java) { NativeReceiver.setAdmit(-1, true) }
        assertThrows(IllegalArgumentException::class.java) { NativeReceiver.airplayTxt(4) }
    }

    private object NoopListener : ReceiverListener {
        override fun onConnectionOpened() {}
        override fun onConnectionClosed() {}
        override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) {}
        override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) {}
        override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) {}
        override fun onAudioVolume(volumeDb: Float) {}
        override fun onVideoReset() {}
    }
}
