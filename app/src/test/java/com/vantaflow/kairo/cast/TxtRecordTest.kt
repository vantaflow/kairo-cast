package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TxtRecordTest {
    private fun b(vararg v: Any): ByteArray =
        ByteArray(v.size) { i -> (v[i] as? Char)?.code?.toByte() ?: (v[i] as Int).toByte() }

    @Test
    fun parsesKeyValuePairs() {
        val raw = b(5, 'd', 'e', 'v', '=', '1', 4, 'f', 'l', '=', 'x')
        assertEquals(mapOf("dev" to "1", "fl" to "x"), TxtRecord.parse(raw))
    }

    @Test
    fun entryWithoutEqualsHasEmptyValue() {
        val raw = b(3, 'a', 'b', 'c')
        assertEquals(mapOf("abc" to ""), TxtRecord.parse(raw))
    }

    @Test
    fun emptyArrayGivesEmptyMap() {
        assertTrue(TxtRecord.parse(ByteArray(0)).isEmpty())
    }

    @Test
    fun lengthPastEndStopsWithoutException() {
        val raw = b(3, 'a', '=', 'b', 9, 'x', '=', 'y')
        assertEquals(mapOf("a" to "b"), TxtRecord.parse(raw))
    }

    @Test
    fun valueMayContainEquals() {
        val raw = b(5, 'k', '=', 'a', '=', 'b')
        assertEquals(mapOf("k" to "a=b"), TxtRecord.parse(raw))
    }
}
