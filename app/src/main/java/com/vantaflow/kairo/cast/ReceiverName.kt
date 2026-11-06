package com.vantaflow.kairo.cast

object ReceiverName {
    private const val MAX_LENGTH = 40

    fun normalize(raw: String?): String {
        val cleaned = (raw ?: "")
            .filter { it != '@' && !Character.isISOControl(it) }
            .trim()
            .take(MAX_LENGTH)
            .trim()
        return cleaned.ifEmpty { DEFAULT_RECEIVER_NAME }
    }
}
