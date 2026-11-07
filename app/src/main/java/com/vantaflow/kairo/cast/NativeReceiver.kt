package com.vantaflow.kairo.cast

/**
 * Serveurs AirPlay natifs, un par emplacement (0 until [MAX_SLOTS]), indépendants.
 * Ne jamais appeler [start]/[stop]/les accesseurs TXT depuis un rappel de [ReceiverListener]
 * (le rappel tourne sur un thread que l'arrêt attend : interblocage).
 */
object NativeReceiver {
    /** Chargée au premier appel natif : les fonctions pures restent testables sur la JVM. */
    private val library by lazy { System.loadLibrary("kairocast") }

    private fun loaded() { library }

    fun requireSlot(slot: Int) {
        require(slot in 0 until MAX_SLOTS) { "emplacement hors limites : $slot" }
    }

    /** Ordre attendu par le natif : [http, miroir, timing, contrôle, données]. */
    fun nativePortsArray(ports: SlotPorts): IntArray =
        intArrayOf(ports.airplayHttp, ports.airplayMirror, ports.udpTiming, ports.udpControl, ports.udpData)

    /**
     * Démarre le serveur de l'emplacement [slot] (n'arrête que celui-ci s'il tournait).
     * deviceId = "aa:bb:cc:dd:ee:ff" ; maxFps <= 0 : défaut d'UxPlay (30).
     */
    fun start(
        slot: Int, name: String, deviceId: String, keyFile: String, width: Int, height: Int, maxFps: Int,
        ports: SlotPorts, listener: ReceiverListener,
    ): Boolean {
        requireSlot(slot)
        loaded()
        return nativeStart(slot, name, deviceId, keyFile, width, height, maxFps, nativePortsArray(ports), listener)
    }

    fun stop(slot: Int) { requireSlot(slot); loaded(); nativeStop(slot) }

    /** Admission des nouveaux clients de l'emplacement (vrai par défaut). */
    fun setAdmit(slot: Int, allow: Boolean) { requireSlot(slot); loaded(); nativeSetAdmit(slot, allow) }

    fun airplayTxt(slot: Int): ByteArray { requireSlot(slot); loaded(); return nativeAirplayTxt(slot) }

    fun raopTxt(slot: Int): ByteArray { requireSlot(slot); loaded(); return nativeRaopTxt(slot) }

    /** "<HEX12>@<name>" */
    fun raopServiceName(slot: Int): String { requireSlot(slot); loaded(); return nativeRaopServiceName(slot) }

    fun version(): String { loaded(); return nativeVersion() }

    private external fun nativeVersion(): String
    private external fun nativeStart(
        slot: Int, name: String, deviceId: String, keyFile: String, width: Int, height: Int, maxFps: Int,
        ports: IntArray, listener: ReceiverListener,
    ): Boolean
    private external fun nativeStop(slot: Int)
    private external fun nativeSetAdmit(slot: Int, allow: Boolean)
    private external fun nativeAirplayTxt(slot: Int): ByteArray
    private external fun nativeRaopTxt(slot: Int): ByteArray
    private external fun nativeRaopServiceName(slot: Int): String
}
