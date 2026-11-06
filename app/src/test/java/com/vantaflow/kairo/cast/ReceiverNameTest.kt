package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Test

class ReceiverNameTest {
    @Test fun nullOrBlank() {
        assertEquals("Kairo", ReceiverName.normalize(null))
        assertEquals("Kairo", ReceiverName.normalize("  "))
    }

    @Test fun removesAt() = assertEquals("SalonTV", ReceiverName.normalize("Salon@TV"))
    @Test fun keepsAccents() = assertEquals("Télé du salon", ReceiverName.normalize("Télé du salon"))
    @Test fun removesControl() = assertEquals("AB", ReceiverName.normalize("A\u0007B\n"))
    @Test fun truncates() = assertEquals(40, ReceiverName.normalize("a".repeat(60)).length)
}
