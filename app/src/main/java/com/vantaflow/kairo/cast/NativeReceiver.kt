package com.vantaflow.kairo.cast

object NativeReceiver {
    const val AIRPLAY_PORT = 7000

    init {
        System.loadLibrary("kairocast")
    }

    /** Démarre le serveur AirPlay. deviceId = "aa:bb:cc:dd:ee:ff". */
    fun start(name: String, deviceId: String, keyFile: String, listener: ReceiverListener): Boolean =
        nativeStart(name, deviceId, keyFile, listener)

    fun stop() = nativeStop()

    fun airplayTxt(): ByteArray = nativeAirplayTxt()

    fun raopTxt(): ByteArray = nativeRaopTxt()

    /** "<HEX12>@<name>" */
    fun raopServiceName(): String = nativeRaopServiceName()

    external fun nativeVersion(): String
    private external fun nativeStart(name: String, deviceId: String, keyFile: String, listener: ReceiverListener): Boolean
    private external fun nativeStop()
    private external fun nativeAirplayTxt(): ByteArray
    private external fun nativeRaopTxt(): ByteArray
    private external fun nativeRaopServiceName(): String
}
