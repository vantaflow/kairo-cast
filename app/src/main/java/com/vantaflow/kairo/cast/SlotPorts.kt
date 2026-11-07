package com.vantaflow.kairo.cast

const val MAX_SLOTS = 4

/** Ports d'un emplacement ; les tables des emplacements sont strictement disjointes. */
data class SlotPorts(
    val airplayHttp: Int,
    val airplayMirror: Int,
    val udpTiming: Int,
    val udpControl: Int,
    val udpData: Int,
    val link: Int,
) {
    val all: List<Int> get() = listOf(airplayHttp, airplayMirror, udpTiming, udpControl, udpData, link)

    companion object {
        fun of(slot: Int): SlotPorts {
            require(slot in 0 until MAX_SLOTS) { "emplacement hors limites : $slot" }
            val step = 10 * slot
            return SlotPorts(7000 + step, 7100 + step, 7011 + step, 6001 + step, 6000 + step, 7300 + slot)
        }
    }
}
