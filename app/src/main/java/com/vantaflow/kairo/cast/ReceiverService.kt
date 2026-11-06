package com.vantaflow.kairo.cast

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import java.io.File

class ReceiverService : Service() {
    private val main = Handler(Looper.getMainLooper())
    private var advertiser: MdnsAdvertiser? = null
    private var running = false

    private val session = ReceiverSession(
        onStart = {
            main.post {
                startActivity(
                    Intent(this, MirrorActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                )
            }
        },
        onStop = {
            main.post { sendBroadcast(Intent(ACTION_CLOSE_MIRROR).setPackage(packageName)) }
        },
    )

    private val listener = object : ReceiverListener {
        override fun onConnectionOpened() {
            session.opened()
            MirrorBus.sink?.onConnectionOpened()
        }
        override fun onConnectionClosed() {
            session.closed()
            MirrorBus.sink?.onConnectionClosed()
            ReceiverState.set(Status.Waiting)
        }
        override fun onVideoFormat(width: Int, height: Int) {
            ReceiverState.set(Status.Mirroring(width, height))
            MirrorBus.sink?.onVideoFormat(width, height)
        }
        override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) {
            MirrorBus.sink?.onVideoData(data, size, ptsUs)
        }
        override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long) {
            MirrorBus.sink?.onAudioData(data, size, ptsUs)
        }
        override fun onAudioVolume(volumeDb: Float) {
            MirrorBus.sink?.onAudioVolume(volumeDb)
        }
        override fun onVideoReset() {
            MirrorBus.sink?.onVideoReset()
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        enterForeground()
        if (intent?.action == ACTION_RESTART) stopReceiver()
        if (!running) startReceiver()
        return START_STICKY
    }

    private fun enterForeground() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Récepteur AirPlay", NotificationManager.IMPORTANCE_LOW)
        )
        val n = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setContentTitle(getString(R.string.app_name))
            .setContentText("Prêt à recevoir")
            .setOngoing(true)
            .build()
        startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
    }

    private fun startReceiver() {
        val settings = CastSettings(PrefsStore(this))
        val name = settings.name
        val ok = NativeReceiver.start(name, settings.deviceId, File(filesDir, "airplay.key").path, listener)
        if (!ok) {
            Log.e(TAG, "démarrage du serveur natif échoué")
            ReceiverState.set(Status.Stopped)
            return
        }
        running = true
        val adv = MdnsAdvertiser(this)
        adv.register(
            name,
            NativeReceiver.raopServiceName(),
            NativeReceiver.AIRPLAY_PORT,
            TxtRecord.parse(NativeReceiver.airplayTxt()),
            TxtRecord.parse(NativeReceiver.raopTxt()),
        )
        advertiser = adv
        ReceiverState.set(Status.Waiting)
        Log.i(TAG, "récepteur démarré : $name")
    }

    private fun stopReceiver() {
        if (!running) return
        advertiser?.unregister()
        advertiser = null
        NativeReceiver.stop()
        running = false
        session.closed()
        ReceiverState.set(Status.Stopped)
    }

    override fun onDestroy() {
        stopReceiver()
        super.onDestroy()
    }

    companion object {
        const val ACTION_RESTART = "com.vantaflow.kairo.cast.RESTART"
        const val ACTION_CLOSE_MIRROR = "com.vantaflow.kairo.cast.CLOSE_MIRROR"
        private const val CHANNEL = "receiver"
        private const val NOTIF_ID = 1
        private const val TAG = "KairoCast"
    }
}
