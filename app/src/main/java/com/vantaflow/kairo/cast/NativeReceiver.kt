package com.vantaflow.kairo.cast

object NativeReceiver {
    init {
        System.loadLibrary("kairocast")
    }

    external fun nativeVersion(): String
}
