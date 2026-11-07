package com.vantaflow.kairo.cast.debug

import android.app.Activity
import android.app.Service
import android.content.Intent
import android.graphics.SurfaceTexture
import android.os.Bundle
import android.os.IBinder
import android.util.Log
import android.view.Gravity
import android.view.Surface
import android.view.TextureView
import android.widget.FrameLayout
import android.widget.LinearLayout
import com.vantaflow.kairo.cast.CastSettings
import com.vantaflow.kairo.cast.Frame
import com.vantaflow.kairo.cast.MdnsAdvertiser
import com.vantaflow.kairo.cast.NativeReceiver
import com.vantaflow.kairo.cast.PrefsStore
import com.vantaflow.kairo.cast.ReceiverListener
import com.vantaflow.kairo.cast.SlotPorts
import com.vantaflow.kairo.cast.TxtRecord
import com.vantaflow.kairo.cast.VideoDecoder
import java.io.File

private const val T = "KairoCastS2"

/** Spike S2 (debug) : deux emplacements UxPlay dans un processus, un décodeur par emplacement. */
object S2Sinks {
    val decoders = arrayOfNulls<VideoDecoder>(2)
    val sizes = arrayOf(intArrayOf(0, 0), intArrayOf(0, 0))
}

class SpikeS2Service : Service() {
    private val advs = HashMap<Int, MdnsAdvertiser>()

    override fun onBind(i: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val refuse1 = intent?.getBooleanExtra("refuse1", false) ?: false
        Thread {
            val settings = CastSettings(PrefsStore(this))
            for (slot in 0..1) startSlot(slot, settings, refuse1 && slot == 1)
        }.start()
        return START_NOT_STICKY
    }

    private fun startSlot(slot: Int, settings: CastSettings, refuse: Boolean) {
        if (advs.containsKey(slot)) return
        val name = settings.slotName(slot)
        val ports = SlotPorts.of(slot)
        val listener = object : ReceiverListener {
            var frames = 0L
            override fun onConnectionOpened() { Log.i(T, "[$slot] connexion ouverte") }
            override fun onConnectionClosed() { Log.i(T, "[$slot] connexion fermée") }
            override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) {
                Log.i(T, "[$slot] onVideoFormat $width x $height contenu $contentX,$contentY ${contentW}x$contentH")
                S2Sinks.sizes[slot][0] = width; S2Sinks.sizes[slot][1] = height
                S2Sinks.decoders[slot]?.setVideoSize(width, height)
            }
            override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) {
                if (frames == 0L) Log.i(T, "[$slot] première image reçue ($size octets)")
                frames++
                if (frames % 300 == 0L) Log.i(T, "[$slot] images reçues $frames")
                S2Sinks.decoders[slot]?.feed(Frame(data.copyOf(size), size, ptsUs, false))
            }
            override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) {}
            override fun onAudioVolume(volumeDb: Float) {}
            override fun onVideoReset() { Log.i(T, "[$slot] reset vidéo") }
        }
        try {
            NativeReceiver.setAdmit(slot, !refuse)
            val ok = NativeReceiver.start(slot, name, settings.slotDeviceId(slot),
                File(filesDir, "s2-airplay-$slot.key").path, 1920, 1080, 30, ports, listener)
            Log.i(T, "[$slot] start \"$name\" = $ok ports=${ports.all} admit=${!refuse}")
            if (ok) {
                val adv = MdnsAdvertiser(applicationContext)
                adv.register(name, NativeReceiver.raopServiceName(slot), ports.airplayHttp,
                    TxtRecord.parse(NativeReceiver.airplayTxt(slot)), TxtRecord.parse(NativeReceiver.raopTxt(slot)))
                advs[slot] = adv
            }
        } catch (e: Throwable) {
            Log.e(T, "[$slot] échec : $e")
        }
    }

    override fun onDestroy() {
        for (slot in 0..1) {
            advs.remove(slot)?.unregister()
            try { NativeReceiver.stop(slot) } catch (e: Throwable) { Log.e(T, "[$slot] arrêt : $e") }
            File(filesDir, "s2-airplay-$slot.key").delete()
        }
        Log.i(T, "service arrêté")
    }
}

class SpikeS2Activity : Activity() {
    override fun onCreate(b: Bundle?) {
        super.onCreate(b)
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setBackgroundColor(0xFF000000.toInt()) }
        for (slot in 0..1) {
            val tv = TextureView(this)
            tv.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
                override fun onSurfaceTextureAvailable(st: SurfaceTexture, w: Int, h: Int) {
                    S2Sinks.decoders[slot] = VideoDecoder(Surface(st)).also {
                        val s = S2Sinks.sizes[slot]; it.setVideoSize(s[0], s[1])
                    }
                    Log.i(T, "[$slot] surface prête ${w}x$h")
                }
                override fun onSurfaceTextureSizeChanged(st: SurfaceTexture, w: Int, h: Int) {}
                override fun onSurfaceTextureDestroyed(st: SurfaceTexture): Boolean {
                    S2Sinks.decoders[slot]?.release(); S2Sinks.decoders[slot] = null
                    return true
                }
                override fun onSurfaceTextureUpdated(st: SurfaceTexture) {}
            }
            val cell = FrameLayout(this)
            cell.addView(tv, FrameLayout.LayoutParams(-1, -1, Gravity.CENTER))
            row.addView(cell, LinearLayout.LayoutParams(0, -1, 1f).also { it.setMargins(2, 0, 2, 0) })
        }
        setContentView(row)
        startService(Intent(this, SpikeS2Service::class.java))
    }
}
