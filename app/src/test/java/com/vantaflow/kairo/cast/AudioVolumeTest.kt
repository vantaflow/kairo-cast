package com.vantaflow.kairo.cast

import org.junit.Assert.assertEquals
import org.junit.Test

class AudioVolumeTest {
    @Test fun muetEnMoinsCentQuaranteQuatre() = assertEquals(0f, AudioPlayer.gainFor(-144f), 0f)
    @Test fun pleinVolumeADb0() = assertEquals(1f, AudioPlayer.gainFor(0f), 0f)
    @Test fun moinsVingtDbVautUnDixieme() = assertEquals(0.1f, AudioPlayer.gainFor(-20f), 0.001f)
    @Test fun sousMoinsTrenteMuet() {
        assertEquals(0f, AudioPlayer.gainFor(-30f), 0f)
        assertEquals(0f, AudioPlayer.gainFor(-31f), 0f)
    }
    @Test fun auDessusDeZeroPlafonne() = assertEquals(1f, AudioPlayer.gainFor(5f), 0f)
    @Test fun nanMuet() = assertEquals(0f, AudioPlayer.gainFor(Float.NaN), 0f)
}
