package com.vantaflow.kairo.cast

import java.util.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DeviceIdentityTest {
    @Test fun format() {
        val re = Regex("^([0-9a-f]{2}:){5}[0-9a-f]{2}$")
        for (seed in 0L..50L) {
            val id = DeviceIdentity.generate(Random(seed))
            assertTrue(id, re.matches(id))
            val first = id.substring(0, 2).toInt(16)
            assertEquals(0x02, first and 0x03)
        }
    }

    @Test fun differentSeeds() {
        assertNotEquals(DeviceIdentity.generate(Random(1)), DeviceIdentity.generate(Random(2)))
    }
}
