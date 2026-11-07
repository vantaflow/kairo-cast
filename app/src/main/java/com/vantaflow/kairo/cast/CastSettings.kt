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
enum class StreamQuality(val width: Int, val height: Int) { HD(1920, 1080), QHD(2560, 1440), UHD(3840, 2160) }

class CastSettings(
    private val store: KeyValueStore,
    private val random: Random = SecureRandom(),
) {
    var name: String
        get() = ReceiverName.normalize(store.getString(KEY_NAME))
        set(value) = store.putString(KEY_NAME, ReceiverName.normalize(value))

    var displayMode: DisplayMode
        get() = DisplayMode.values().firstOrNull { it.name == store.getString(KEY_MODE) } ?: DisplayMode.STRETCH
        set(value) = store.putString(KEY_MODE, value.name)

    var quality: StreamQuality
        get() = StreamQuality.values().firstOrNull { it.name == store.getString(KEY_QUALITY) } ?: StreamQuality.QHD
        set(value) = store.putString(KEY_QUALITY, value.name)

    var audioEnabled: Boolean
        get() = store.getString(KEY_AUDIO) != "false"
        set(value) = store.putString(KEY_AUDIO, value.toString())

    var startOnBoot: Boolean
        get() = store.getString(KEY_BOOT) != "false"
        set(value) = store.putString(KEY_BOOT, value.toString())

    val deviceId: String
        get() = synchronized(this) {
            store.getString(KEY_DEVICE_ID) ?: DeviceIdentity.generate(random).also {
                store.putString(KEY_DEVICE_ID, it)
            }
        }

    private companion object {
        const val KEY_NAME = "name"
        const val KEY_MODE = "display_mode"
        const val KEY_QUALITY = "stream_quality"
        const val KEY_AUDIO = "audio_enabled"
        const val KEY_BOOT = "start_on_boot"
        const val KEY_DEVICE_ID = "device_id"
    }
}
