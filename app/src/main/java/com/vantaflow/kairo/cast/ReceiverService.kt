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
import com.vantaflow.kairo.cast.link.Hello
import com.vantaflow.kairo.cast.link.LinkListener
import com.vantaflow.kairo.cast.link.LinkServer
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

    private val arb = Any()
    @Volatile private var owner = MirrorSource.NONE
    private var linkServer: LinkServer? = null
    private var ignoredLogged = false
    @Volatile private var airplayConnected = false // connexion native AirPlay ouverte (même ignorée)

    /** Image clé demandée par le décodeur ou l'activité : utile seulement pour Link. */
    private fun keyFrameNeeded() {
        if (owner == MirrorSource.LINK) linkServer?.requestKeyFrame()
    }

    /** Prend la main (sous [arb]) ; renvoie la source précédente. */
    private fun beginSession(src: MirrorSource): MirrorSource {
        synchronized(arb) {
            val prev = owner
            owner = src
            ignoredLogged = false
            val p = MirrorBus.pipeline
            when (SessionArbiter.action(prev, src, p != null)) {
                PipelineAction.START_FRESH ->
                    MirrorBus.start(CastSettings(PrefsStore(this)).audioEnabled, ::keyFrameNeeded)
                PipelineAction.SWITCH_SOURCE -> p?.onSourceChanged()
            }
            session.opened()
            return prev
        }
    }

    private fun endSession(src: MirrorSource) {
        synchronized(arb) {
            if (owner != src) return
            owner = MirrorSource.NONE
            session.closed()
            MirrorBus.stop()
            // Le Mac est resté connecté mais AirPlay ne permet pas de redemander une image clé.
            ReceiverState.set(if (src == MirrorSource.LINK && airplayConnected) Status.MacWaiting else Status.Waiting)
        }
    }

    /** Vrai si les données AirPlay doivent être traitées ; sinon (Link actif) elles sont ignorées. */
    private fun airplayActive(): Boolean {
        if (!accepting) return false
        if (owner == MirrorSource.AIRPLAY) return true
        if (owner == MirrorSource.LINK && !ignoredLogged) {
            ignoredLogged = true
            Log.i(TAG, "AirPlay : données ignorées tant que Link est actif")
        }
        return false
    }

    private fun takeOverFromLink() {
        // Hors de [arb] : closeClient prend le verrou de dispatch du serveur Link.
        linkServer?.closeClient()
    }

    private val listener = object : ReceiverListener {
        override fun onConnectionOpened() {
            if (!accepting) return
            airplayConnected = true
            val prev = beginSession(MirrorSource.AIRPLAY)
            if (prev == MirrorSource.LINK) {
                Log.i(TAG, "AirPlay arrive : la session Link est fermée")
                takeOverFromLink()
            }
        }
        override fun onConnectionClosed() {
            if (!accepting) return
            airplayConnected = false
            endSession(MirrorSource.AIRPLAY)
            if (ReceiverState.status.value == Status.MacWaiting) ReceiverState.set(Status.Waiting)
        }
        override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) {
            if (!accepting) return
            // Un nouveau flux AirPlay est une arrivée, même si une connexion était restée ouverte.
            if (owner != MirrorSource.AIRPLAY) {
                val prev = beginSession(MirrorSource.AIRPLAY)
                if (prev == MirrorSource.LINK) {
                    Log.i(TAG, "AirPlay arrive : la session Link est fermée")
                    takeOverFromLink()
                }
            }
            Log.i(TAG, "onVideoFormat flux ${width}x$height contenu x=$contentX y=$contentY ${contentW}x$contentH")
            ReceiverState.set(Status.Mirroring(width, height))
            MirrorBus.pipeline?.onVideoFormat(width, height, contentX, contentY, contentW, contentH)
        }
        override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) {
            if (!airplayActive()) return
            MirrorBus.pipeline?.onVideoData(data, size, ptsUs)
        }
        override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) {
            if (!airplayActive()) return
            MirrorBus.pipeline?.onAudioData(data, size, ptsUs, codecType)
        }
        override fun onAudioVolume(volumeDb: Float) {
            if (!accepting) return
            MirrorBus.pipeline?.onAudioVolume(volumeDb)
        }
        override fun onVideoReset() {
            if (!airplayActive()) return
            MirrorBus.pipeline?.onVideoReset()
        }
    }

    private val linkListener = object : LinkListener {
        override fun onLinkOpened(hello: Hello) {
            if (!accepting) return
            beginSession(MirrorSource.LINK)
            Log.i(TAG, "Link : session ouverte (${hello.name} ${hello.w}x${hello.h})")
            ReceiverState.set(Status.Mirroring(hello.w, hello.h))
            MirrorBus.pipeline?.onVideoFormat(hello.w, hello.h, 0, 0, hello.w, hello.h)
        }
        override fun onLinkVideo(data: ByteArray, size: Int, ptsUs: Long, key: Boolean) {
            if (owner != MirrorSource.LINK) return
            MirrorBus.pipeline?.onVideoData(data, size, ptsUs)
        }
        override fun onLinkAudio(pcm: ByteArray, size: Int, ptsUs: Long) {
            if (owner != MirrorSource.LINK) return
            MirrorBus.pipeline?.onPcmAudio(pcm, size, ptsUs)
        }
        override fun onLinkClosed() {
            Log.i(TAG, "Link : session fermée")
            endSession(MirrorSource.LINK)
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
        val ok = NativeReceiver.start(0, name, settings.deviceId, File(filesDir, "airplay.key").path,
            settings.quality.width, settings.quality.height, 0, SlotPorts.of(0), listener)
        if (!ok) {
            Log.e(TAG, "démarrage du serveur natif échoué")
            accepting = false
            setNotificationText("Erreur : récepteur indisponible")
            ReceiverState.set(Status.Stopped)
            return
        }
        running = true
        var linkPort = 0
        try {
            val ls = LinkServer(LinkServer.DEFAULT_PORT, linkListener)
            ls.start()
            linkServer = ls
            linkPort = LinkServer.DEFAULT_PORT
        } catch (e: Exception) {
            Log.e(TAG, "serveur Link indisponible (port ${LinkServer.DEFAULT_PORT}) : $e")
        }
        val adv = MdnsAdvertiser(this)
        adv.register(
            name,
            NativeReceiver.raopServiceName(0),
            SlotPorts.of(0).airplayHttp,
            TxtRecord.parse(NativeReceiver.airplayTxt(0)),
            TxtRecord.parse(NativeReceiver.raopTxt(0)),
            linkPort,
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
        linkServer?.stop()
        linkServer = null
        NativeReceiver.stop(0)
        running = false
        synchronized(arb) {
            owner = MirrorSource.NONE
            airplayConnected = false
            session.closed()
            MirrorBus.stop()
        }
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
