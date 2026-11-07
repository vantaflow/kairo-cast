package com.vantaflow.kairo.cast

/** Clés de stockage et noms de fichiers par emplacement ; l'emplacement 0 garde les clés historiques. */
object SlotIdentity {
    fun nameKey(slot: Int): String = if (slot == 0) "name" else "slot${slot}_name"
    fun deviceIdKey(slot: Int): String = if (slot == 0) "device_id" else "slot${slot}_device_id"
    fun modeKey(slot: Int): String = if (slot == 0) "display_mode" else "slot${slot}_display_mode"
    fun keyFileName(slot: Int): String = if (slot == 0) "airplay.key" else "airplay-$slot.key"
    fun defaultName(slot: Int): String =
        ReceiverName.normalize(if (slot == 0) DEFAULT_RECEIVER_NAME else "$DEFAULT_RECEIVER_NAME ${slot + 1}")
}
