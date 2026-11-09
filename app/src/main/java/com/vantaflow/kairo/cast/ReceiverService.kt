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

    /** Qui a l'écran : AirPlay et Kairo Link s'excluent. */
    private val arbiter = SourceArbiter()
    private var link: LinkServer? = null // worker uniquement

    private fun startMirror() {
        MirrorBus.start(CastSettings(PrefsStore(this)).audioEnabled)
        session.opened()
    }

    private fun stopMirror() {
        session.closed()
        MirrorBus.stop()
    }

    /** Relais vers le pipeline, filtré par [arbiter]. */
    private fun relay(src: SourceArbiter.Source) = object : ReceiverListener {
        private fun mine(): Boolean = accepting && arbiter.accepts(src)

        override fun onConnectionOpened() {
            if (!accepting) return
            // Kairo Link a déjà réservé l'écran (tryClaimLink) avant d'accepter le PC.
            if (src == SourceArbiter.Source.LINK || arbiter.airplayOpened()) startMirror()
        }
        override fun onConnectionClosed() {
            if (!accepting) return
            if (src == SourceArbiter.Source.LINK) {
                stopMirror()
                if (arbiter.linkClosed()) {
                    Log.i(TAG, "fin de la session Kairo Link : le Mac connecté reprend la main")
                    startMirror()
                    return
                }
            } else {
                if (!arbiter.airplayClosed()) return
                stopMirror()
            }
            ReceiverState.set(Status.Waiting)
        }
        override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) {
            if (!mine()) return
            Log.i(TAG, "onVideoFormat flux ${width}x$height contenu x=$contentX y=$contentY ${contentW}x$contentH")
            ReceiverState.set(Status.Mirroring(width, height))
            MirrorBus.pipeline?.onVideoFormat(width, height, contentX, contentY, contentW, contentH)
        }
        override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) {
            if (!mine()) return
            MirrorBus.pipeline?.onVideoData(data, size, ptsUs)
        }
        override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) {
            if (!mine()) return
            MirrorBus.pipeline?.onAudioData(data, size, ptsUs, codecType)
        }
        override fun onAudioVolume(volumeDb: Float) {
            if (!mine()) return
            MirrorBus.pipeline?.onAudioVolume(volumeDb)
        }
        override fun onVideoReset() {
            if (!mine()) return
            MirrorBus.pipeline?.onVideoReset()
        }
    }

    private val listener = relay(SourceArbiter.Source.AIRPLAY)

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
        val ls = LinkServer(
            relay(SourceArbiter.Source.LINK),
            onPcmFormat = { rate, ch ->
                if (accepting && arbiter.accepts(SourceArbiter.Source.LINK)) MirrorBus.pipeline?.onPcmFormat(rate, ch)
            },
            claim = { accepting && arbiter.tryClaimLink() },
            limits = {
                val q = CastSettings(PrefsStore(this)).quality
                Triple(q.width, q.height, CastSettings(PrefsStore(this)).audioEnabled)
            },
        )
        val linkOk = ls.start()
        if (linkOk) link = ls else Log.w(TAG, "Kairo Link indisponible (port ${LinkProtocol.PORT})")
        val adv = MdnsAdvertiser(this)
        adv.register(
            name,
            NativeReceiver.raopServiceName(),
            NativeReceiver.AIRPLAY_PORT,
            TxtRecord.parse(NativeReceiver.airplayTxt()),
            TxtRecord.parse(NativeReceiver.raopTxt()),
            linkPort = if (linkOk) LinkProtocol.PORT else 0,
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
        link?.stop()
        link = null
        NativeReceiver.stop()
        running = false
        session.closed()
        MirrorBus.stop()
        arbiter.reset()
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
