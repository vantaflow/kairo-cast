package com.vantaflow.kairo.cast

import android.content.Context
import java.security.SecureRandom
import java.util.Random

interface KeyValueStore {
    fun getString(k: String): String?
    fun putString(k: String, v: String)
}

class PrefsStore(ctx: Context) : KeyValueStore {
    private val prefs = ctx.getSharedPreferences("kairo_cast", Context.MODE_PRIVATE)
    override fun getString(k: String): String? = prefs.getString(k, null)
    override fun putString(k: String, v: String) {
        prefs.edit().putString(k, v).apply()
    }
}

/** Résolution annoncée au Mac (plus haute = image plus nette). */
enum class StreamQuality(val width: Int, val height: Int) { HD(1920, 1200), QHD(2560, 1600), UHD(3456, 2160) }

/** Mosaïque : tuiles qui remplissent l'écran, ou formats exacts des sources (barres possibles). */
enum class MosaicMode { FILL_SCREEN, EXACT_FORMATS }

sealed interface NameResult {
    object Ok : NameResult
    object Duplicate : NameResult
}

class CastSettings(
    private val store: KeyValueStore,
    private val random: Random = SecureRandom(),
) {
    var name: String
        get() = slotName(0)
        set(value) { setSlotName(0, value) }

    var displayMode: DisplayMode
        get() = slotDisplayMode(0)
        set(value) = setSlotDisplayMode(0, value)

    var slotCount: Int
        get() = store.getString(KEY_SLOT_COUNT)?.toIntOrNull()?.takeIf { it in ALLOWED_SLOT_COUNTS } ?: 1
        set(value) = store.putString(KEY_SLOT_COUNT, (if (value in ALLOWED_SLOT_COUNTS) value else 1).toString())

    var mosaicMode: MosaicMode
        get() = MosaicMode.values().firstOrNull { it.name == store.getString(KEY_MOSAIC) } ?: MosaicMode.FILL_SCREEN
        set(value) = store.putString(KEY_MOSAIC, value.name)

    /** Source audio épinglée (null = le son suit la tuile sélectionnée). */
    var audioPin: Int?
        get() = store.getString(KEY_AUDIO_PIN)?.toIntOrNull()?.takeIf { it in 0 until MAX_SLOTS }
        set(value) = store.putString(KEY_AUDIO_PIN, value?.takeIf { it in 0 until MAX_SLOTS }?.toString() ?: "follow")

    fun slotName(slot: Int): String {
        checkSlot(slot)
        val raw = store.getString(SlotIdentity.nameKey(slot)) ?: return SlotIdentity.defaultName(slot)
        return ReceiverName.normalize(raw)
    }

    fun setSlotName(slot: Int, raw: String): NameResult = synchronized(this) {
        checkSlot(slot)
        val blank = raw.filter { it != '@' && !Character.isISOControl(it) }.isBlank()
        val name = if (blank) SlotIdentity.defaultName(slot) else ReceiverName.normalize(raw)
        val taken = (0 until slotCount).any { it != slot && slotName(it).equals(name, ignoreCase = true) }
        if (taken) return NameResult.Duplicate
        store.putString(SlotIdentity.nameKey(slot), name)
        NameResult.Ok
    }

    fun slotDeviceId(slot: Int): String = synchronized(this) {
        checkSlot(slot)
        val key = SlotIdentity.deviceIdKey(slot)
        store.getString(key) ?: DeviceIdentity.generate(random).also { store.putString(key, it) }
    }

    fun slotDisplayMode(slot: Int): DisplayMode {
        checkSlot(slot)
        val stored = store.getString(SlotIdentity.modeKey(slot))
        return DisplayMode.values().firstOrNull { it.name == stored }
            ?: if (slotCount >= 2) DisplayMode.FIT else DisplayMode.STRETCH
    }

    fun setSlotDisplayMode(slot: Int, m: DisplayMode) {
        checkSlot(slot)
        store.putString(SlotIdentity.modeKey(slot), m.name)
    }

    val slotNames: List<String>
        get() = (0 until slotCount).map { slotName(it) }

    private fun checkSlot(slot: Int) = require(slot in 0 until MAX_SLOTS) { "emplacement hors limites : $slot" }

    var quality: StreamQuality
        get() = StreamQuality.values().firstOrNull { it.name == store.getString(KEY_QUALITY) } ?: StreamQuality.QHD
        set(value) = store.putString(KEY_QUALITY, value.name)

    var audioEnabled: Boolean
        get() = store.getString(KEY_AUDIO) != "false"
        set(value) = store.putString(KEY_AUDIO, value.toString())

    var startOnBoot: Boolean
        get() = store.getString(KEY_BOOT) != "false"
        set(value) = store.putString(KEY_BOOT, value.toString())

    /** Afficher l'accueil de Kairo Cast quand la TV sort de veille ou démarre (désactivé par défaut). */
    var openOnPowerOn: Boolean
        get() = store.getString(KEY_OPEN_ON_POWER) == "true"
        set(value) = store.putString(KEY_OPEN_ON_POWER, value.toString())

    val deviceId: String
        get() = slotDeviceId(0)

    private companion object {
        const val KEY_QUALITY = "stream_quality"
        const val KEY_AUDIO = "audio_enabled"
        const val KEY_BOOT = "start_on_boot"
        const val KEY_OPEN_ON_POWER = "open_on_power_on"
        const val KEY_MOSAIC = "mosaic_mode"
        const val KEY_AUDIO_PIN = "audio_pin"
        const val KEY_SLOT_COUNT = "slot_count"
        val ALLOWED_SLOT_COUNTS = listOf(1, 2, 3, 4)
    }
}
