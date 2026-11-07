package com.vantaflow.kairo.cast.debug

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AnnexBTest {
    private fun nal(vararg b: Int) = byteArrayOf(0, 0, 0, 1) + b.map { it.toByte() }.toByteArray()

    @Test fun `flux synthetique donne trois unites d'acces`() {
        val sps = nal(0x67, 1, 2)
        val pps = nal(0x68, 3)
        val idr = nal(0x65, 0x88, 9, 9)
        val p1 = nal(0x41, 0x9a, 5)
        val p2 = nal(0x41, 0x9a, 6)
        val idr2 = nal(0x65, 0x88, 7)
        val data = sps + pps + idr + p1 + p2 + idr2
        val aus = AnnexB.splitAccessUnits(data)
        assertEquals(4, aus.size) // SPS+PPS+IDR, P, P, IDR
        assertEquals(0, aus[0].first)
        assertEquals((sps + pps + idr).size - 1, aus[0].last)
        assertEquals(data.size - 1, aus.last().last)
    }

    @Test fun `flux vide`() {
        assertTrue(AnnexB.splitAccessUnits(ByteArray(0)).isEmpty())
    }

    @Test fun `sans start code une seule plage`() {
        assertEquals(listOf(0..4), AnnexB.splitAccessUnits(byteArrayOf(1, 2, 3, 4, 5)))
    }
}
