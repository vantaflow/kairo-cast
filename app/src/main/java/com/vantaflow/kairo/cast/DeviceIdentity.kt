package com.vantaflow.kairo.cast

import java.util.Random

object DeviceIdentity {
    fun generate(random: Random): String {
        val bytes = ByteArray(6).also { random.nextBytes(it) }
        bytes[0] = ((bytes[0].toInt() and 0xFC) or 0x02).toByte()
        return bytes.joinToString(":") { "%02x".format(it.toInt() and 0xFF) }
    }
}
