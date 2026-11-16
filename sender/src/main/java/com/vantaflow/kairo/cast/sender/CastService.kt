package com.vantaflow.kairo.cast.sender

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.Looper
import android.util.Log
import android.view.Display
import android.view.WindowManager
import com.vantaflow.kairo.cast.link.Hello

/**
 * Service de premier plan du partage : capture de l'écran (MediaProjection) → encodeur H.264 → Kairo Link,
 * et son des applis. Quand le téléphone tourne, la session repart avec la nouvelle orientation.
 */
class CastService : Service() {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var worker: Handler
    private var workerThread: HandlerThread? = null

    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var encoder: ScreenEncoder? = null
    @Volatile private var link: LinkSender? = null
    private var audio: AudioCapturer? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var box: Box? = null
    private var deviceName = ""
    private var landscape = false
    @Volatile private var baseUs = 0L
    private var stopping = false

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(id: Int) {}
        override fun onDisplayRemoved(id: Int) {}
        override fun onDisplayChanged(id: Int) {
            if (id != Display.DEFAULT_DISPLAY) return
            val (w, h) = screenSize()
            if ((w >= h) != landscape) worker.post { restart() }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        workerThread = HandlerThread("kairo-envoi").also { it.start(); worker = Handler(it.looper) }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopCast(null)
            return START_NOT_STICKY
        }
        if (projection != null) return START_NOT_STICKY // un seul partage à la fois
        stopping = false
        val b = Box(intent?.getStringExtra(EXTRA_ID) ?: "", intent?.getStringExtra(EXTRA_NAME) ?: "TV",
            intent?.getStringExtra(EXTRA_HOST) ?: "", intent?.getIntExtra(EXTRA_PORT, 0) ?: 0)
        @Suppress("DEPRECATION")
        val data: Intent? = intent?.getParcelableExtra(EXTRA_DATA)
        val code = intent?.getIntExtra(EXTRA_CODE, Activity.RESULT_CANCELED) ?: Activity.RESULT_CANCELED
        if (b.host.isEmpty() || data == null || code != Activity.RESULT_OK) {
            stopSelf()
            return START_NOT_STICKY
        }
        box = b
        deviceName = intent.getStringExtra(EXTRA_DEVICE) ?: Build.MODEL
        val withAudio = intent.getBooleanExtra(EXTRA_AUDIO, true)
        // Le service de premier plan doit exister avant d'obtenir la capture (Android 14).
        startForeground(NOTIF_ID, notification(getString(R.string.notif_connecting, b.name)), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        CastState.set(b, getString(R.string.notif_connecting, b.name))
        val p = try {
            getSystemService(MediaProjectionManager::class.java).getMediaProjection(code, data)
        } catch (e: Exception) {
            Log.w(TAG, "capture refusée", e)
            null
        }
        if (p == null) {
            stopCast(getString(R.string.err_capture))
            return START_NOT_STICKY
        }
        projection = p
        p.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() = main.post { stopCast(null) }.let {}
        }, main)
        @Suppress("DEPRECATION")
        wifiLock = getSystemService(WifiManager::class.java)
            ?.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "kairo-envoi")?.apply { setReferenceCounted(false); acquire() }
        getSystemService(DisplayManager::class.java).registerDisplayListener(displayListener, main)
        worker.post {
            if (!startStream()) return@post
            if (withAudio && checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
                try {
                    audio = AudioCapturer(p).also { a -> a.start { pcm, n, t -> link?.sendAudio(pcm, n, maxOf(0, t - baseUs)) } }
                } catch (e: Exception) {
                    Log.w(TAG, "son indisponible", e)
                }
            }
        }
        return START_NOT_STICKY
    }

    /** Sur le fil de travail : encodeur, écran virtuel et liaison. Faux si le partage a été arrêté. */
    private fun startStream(): Boolean {
        val b = box ?: return false
        val p = projection ?: return false
        val (sw, sh) = screenSize()
        landscape = sw >= sh
        val enc = try { ScreenEncoder.create(sw, sh) } catch (e: Exception) {
            Log.w(TAG, "encodeur", e)
            main.post { stopCast(getString(R.string.err_encoder)) }
            return false
        }
        val dpi = resources.displayMetrics.densityDpi
        val vd = display
        if (vd == null) {
            display = p.createVirtualDisplay("Kairo Cast", enc.width, enc.height, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, enc.surface, null, null)
        } else {
            vd.resize(enc.width, enc.height, dpi)
            vd.surface = enc.surface
        }
        encoder = enc
        val l = LinkSender(b.host, b.port, Hello(1, deviceName, enc.width, enc.height, ScreenEncoder.FPS, AudioCapturer.FORMAT),
            onKeyFrameRequested = { encoder?.requestKeyFrame() },
            onClosed = { reason -> if (reason != null) main.post { if (link != null) stopCast(reason) } })
        try {
            l.connect()
        } catch (e: Exception) {
            Log.w(TAG, "connexion", e)
            main.post { stopCast(getString(R.string.err_unreachable, b.name)) }
            return false
        }
        baseUs = System.nanoTime() / 1000
        link = l
        enc.start(
            onFrame = { au, key, pts -> link?.sendVideo(au, key, maxOf(0, pts - baseUs)) },
            onError = { e -> Log.w(TAG, "encodeur", e); main.post { stopCast(getString(R.string.err_encoder)) } },
        )
        val msg = getString(R.string.notif_sharing, b.name)
        CastState.set(b, msg)
        getSystemService(NotificationManager::class.java).notify(NOTIF_ID, notification(msg))
        return true
    }

    /** Rotation : nouvelle session avec la nouvelle orientation (la box ne change pas de taille en cours de session). */
    private fun restart() {
        if (stopping || projection == null) return
        Log.i(TAG, "rotation : nouvelle session")
        val old = link
        link = null
        old?.close()
        encoder?.release()
        encoder = null
        startStream()
    }

    private fun stopCast(reason: String?) {
        if (stopping) return
        stopping = true
        try { getSystemService(DisplayManager::class.java).unregisterDisplayListener(displayListener) } catch (_: Exception) {}
        val l = link
        link = null
        val a = audio
        audio = null
        worker.post {
            a?.release()
            l?.close()
            encoder?.release()
            encoder = null
            display?.release()
            display = null
            main.post {
                try { projection?.stop() } catch (_: Exception) {}
                projection = null
                wifiLock?.let { try { it.release() } catch (_: Exception) {} }
                CastState.set(null, reason ?: getString(R.string.state_stopped))
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
    }

    override fun onDestroy() {
        workerThread?.quitSafely()
        super.onDestroy()
    }

    private fun screenSize(): Pair<Int, Int> {
        val wm = getSystemService(WindowManager::class.java)
        return if (Build.VERSION.SDK_INT >= 30) {
            val r = wm.maximumWindowMetrics.bounds
            r.width() to r.height()
        } else {
            val m = android.util.DisplayMetrics()
            @Suppress("DEPRECATION") wm.defaultDisplay.getRealMetrics(m)
            m.widthPixels to m.heightPixels
        }
    }

    private fun notification(text: String): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) == null) {
            nm.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.channel), NotificationManager.IMPORTANCE_LOW))
        }
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, CastService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_cast_glyph)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, getString(R.string.stop), stop).build())
            .build()
    }

    companion object {
        private const val TAG = "KairoCastEnvoi"
        private const val CHANNEL = "partage"
        private const val NOTIF_ID = 1
        const val ACTION_STOP = "com.vantaflow.kairo.cast.sender.STOP"
        const val EXTRA_CODE = "code"
        const val EXTRA_DATA = "data"
        const val EXTRA_ID = "id"
        const val EXTRA_NAME = "name"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_DEVICE = "device"
        const val EXTRA_AUDIO = "audio"

        fun start(ctx: Context, box: Box, code: Int, data: Intent, device: String, audio: Boolean) {
            ctx.startForegroundService(Intent(ctx, CastService::class.java)
                .putExtra(EXTRA_CODE, code).putExtra(EXTRA_DATA, data)
                .putExtra(EXTRA_ID, box.id).putExtra(EXTRA_NAME, box.name)
                .putExtra(EXTRA_HOST, box.host).putExtra(EXTRA_PORT, box.port)
                .putExtra(EXTRA_DEVICE, device).putExtra(EXTRA_AUDIO, audio))
        }

        fun stop(ctx: Context) {
            ctx.startService(Intent(ctx, CastService::class.java).setAction(ACTION_STOP))
        }
    }
}
