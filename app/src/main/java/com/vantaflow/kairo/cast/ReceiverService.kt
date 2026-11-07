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
import java.util.concurrent.Executors

class ReceiverService : Service() {
    private val main = Handler(Looper.getMainLooper())
    private var advertiser: MdnsAdvertiser? = null
    private val worker = Executors.newSingleThreadExecutor { Thread(it, "kairo-receiver") }
    @Volatile private var accepting = false
    private var running = false // worker uniquement

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
            if (!accepting) return
            MirrorBus.start(CastSettings(PrefsStore(this@ReceiverService)).audioEnabled)
            session.opened()
        }
        override fun onConnectionClosed() {
            if (!accepting) return
            session.closed()
            MirrorBus.stop()
            ReceiverState.set(Status.Waiting)
        }
        override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) {
            if (!accepting) return
            Log.i(TAG, "onVideoFormat flux ${width}x$height contenu x=$contentX y=$contentY ${contentW}x$contentH")
            ReceiverState.set(Status.Mirroring(width, height))
            MirrorBus.pipeline?.onVideoFormat(width, height, contentX, contentY, contentW, contentH)
        }
        override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) {
            if (!accepting) return
            MirrorBus.pipeline?.onVideoData(data, size, ptsUs)
        }
        override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) {
            if (!accepting) return
            MirrorBus.pipeline?.onAudioData(data, size, ptsUs, codecType)
        }
        override fun onAudioVolume(volumeDb: Float) {
            if (!accepting) return
            MirrorBus.pipeline?.onAudioVolume(volumeDb)
        }
        override fun onVideoReset() {
            if (!accepting) return
            MirrorBus.pipeline?.onVideoReset()
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        enterForeground()
        val restart = intent?.action == ACTION_RESTART
        try {
            worker.execute {
                if (restart) stopReceiver()
                if (!running) startReceiver()
            }
        } catch (e: java.util.concurrent.RejectedExecutionException) {
            Log.w(TAG, "service en arrêt, commande ignorée")
        }
        return START_STICKY
    }

    private fun notification(text: String): Notification =
        Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setOngoing(true)
            .build()

    private fun enterForeground() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Récepteur AirPlay", NotificationManager.IMPORTANCE_LOW)
        )
        startForeground(
            NOTIF_ID, notification("Démarrage…"), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
        )
    }

    private fun setNotificationText(text: String) {
        getSystemService(NotificationManager::class.java).notify(NOTIF_ID, notification(text))
    }

    private fun startReceiver() {
        val settings = CastSettings(PrefsStore(this))
        val name = settings.name
        AudioPlayer.logAvailableDecoders()
        accepting = true
        val ok = NativeReceiver.start(name, settings.deviceId, File(filesDir, "airplay.key").path,
            settings.quality.width, settings.quality.height, listener)
        if (!ok) {
            Log.e(TAG, "démarrage du serveur natif échoué")
            accepting = false
            setNotificationText("Erreur : récepteur indisponible")
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
        setNotificationText("Prêt à recevoir")
        Log.i(TAG, "récepteur démarré : $name")
    }

    private fun stopReceiver() {
        if (!running) return
        accepting = false
        advertiser?.unregister()
        advertiser = null
        NativeReceiver.stop()
        running = false
        session.closed()
        MirrorBus.stop()
        ReceiverState.set(Status.Stopped)
    }

    override fun onDestroy() {
        accepting = false
        main.removeCallbacksAndMessages(null)
        worker.execute { stopReceiver() }
        worker.shutdown()
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
