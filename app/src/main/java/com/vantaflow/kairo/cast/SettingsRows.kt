package com.vantaflow.kairo.cast

data class SettingsRow(val id: String, val enabled: Boolean, val value: String)

object SettingsRows {
    const val QUALITY_FIXED = "fixé par le nombre de sources"

    data class Snapshot(val slotCount: Int, val names: List<String>, val quality: StreamQuality)

    /** Une ligne « name » (valeur = le nom) si 1 source, sinon « name_i » avec « Source n : nom ». */
    fun nameRows(settings: CastSettings): List<SettingsRow> {
        val n = settings.slotCount
        if (n == 1) return listOf(SettingsRow("name", true, settings.slotName(0)))
        return (0 until n).map { SettingsRow("name_$it", true, "Source ${it + 1} : ${settings.slotName(it)}") }
    }

    fun qualityLabel(q: StreamQuality): String = when (q) {
        StreamQuality.HD -> "Standard (1200p)"
        StreamQuality.QHD -> "Haute (1600p)"
        StreamQuality.UHD -> "Maximale (2160p)"
    }

    fun qualityRow(settings: CastSettings): SettingsRow =
        if (settings.slotCount == 1) SettingsRow("quality", true, qualityLabel(settings.quality))
        else SettingsRow("quality", false, QUALITY_FIXED)

    fun nextSlotCount(cur: Int): Int = when (cur) { 1 -> 2; 2 -> 4; else -> 1 }

    fun needsRestart(before: Snapshot, after: Snapshot): Boolean {
        if (before.slotCount != after.slotCount || before.quality != after.quality) return true
        return before.names.take(before.slotCount) != after.names.take(after.slotCount)
    }

    fun snapshot(s: CastSettings) = Snapshot(s.slotCount, (0 until 4).map { s.slotName(it) }, s.quality)
}
