package com.vantaflow.kairo.cast

data class SettingsRow(val id: String, val enabled: Boolean, val value: String)

object SettingsRows {
    /** Textes fournis par l'appelant (ressources strings.xml). */
    interface Labels {
        fun sourceN(n: Int, name: String): String
        fun qualityFixed(): String
        fun audioFollow(): String
        fun quality(q: StreamQuality): String
    }

    data class Snapshot(val slotCount: Int, val names: List<String>, val quality: StreamQuality, val audioPin: Int? = null)

    /** Une ligne « name » (valeur = le nom) si 1 source, sinon « name_i » avec « Source n : nom ». */
    fun nameRows(settings: CastSettings, labels: Labels): List<SettingsRow> {
        val n = settings.slotCount
        if (n == 1) return listOf(SettingsRow("name", true, settings.slotName(0)))
        return (0 until n).map { SettingsRow("name_$it", true, labels.sourceN(it + 1, settings.slotName(it))) }
    }

    fun qualityRow(settings: CastSettings, labels: Labels): SettingsRow =
        if (settings.slotCount == 1) SettingsRow("quality", true, labels.quality(settings.quality))
        else SettingsRow("quality", false, labels.qualityFixed())

    fun nextSlotCount(cur: Int): Int = if (cur >= 4) 1 else cur + 1

    /** null (suit la tuile) → source 0 → … → source count-1 → null. */
    fun nextAudioPin(cur: Int?, count: Int): Int? = when {
        cur == null || cur !in 0 until count -> if (cur == null) 0 else null
        cur + 1 < count -> cur + 1
        else -> null
    }

    /** Ligne « Son des sources » : absente avec une seule source. */
    fun audioRow(settings: CastSettings, labels: Labels): SettingsRow? {
        if (settings.slotCount < 2) return null
        val pin = settings.audioPin?.takeIf { it < settings.slotCount }
        return SettingsRow("audio_source", true, if (pin == null) labels.audioFollow() else labels.sourceN(pin + 1, settings.slotName(pin)))
    }

    fun needsRestart(before: Snapshot, after: Snapshot): Boolean {
        if (before.slotCount != after.slotCount || before.quality != after.quality || before.audioPin != after.audioPin) return true
        return before.names.take(before.slotCount) != after.names.take(after.slotCount)
    }

    fun snapshot(s: CastSettings) = Snapshot(s.slotCount, (0 until 4).map { s.slotName(it) }, s.quality, s.audioPin)
}
