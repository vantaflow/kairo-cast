package com.vantaflow.kairo.cast

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.SurfaceTexture
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.KeyEvent
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.WindowManager
import android.widget.Toast

class MirrorActivity : Activity() {
    private lateinit var texture: TextureView
    private lateinit var settings: CastSettings
    private var pipeline: MirrorPipeline? = null
    private var surface: Surface? = null
    private var videoW = 0
    private var videoH = 0

    private val closeReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            Log.i(TAG, "fermeture demandée (connexion terminée)")
            finish()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_mirror)
        settings = CastSettings(PrefsStore(this))
        texture = findViewById(R.id.texture)
        (ReceiverState.status.value as? Status.Mirroring)?.let {
            videoW = it.width
            videoH = it.height
        }
        texture.addOnLayoutChangeListener { _, l, t, r, b, ol, ot, orr, ob ->
            if (r - l != orr - ol || b - t != ob - ot) applyTransform()
        }
        texture.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(st: SurfaceTexture, w: Int, h: Int) {
                val s = Surface(st)
                surface = s
                val size = if (videoW > 0 && videoH > 0) videoW to videoH else null
                val p = MirrorPipeline(s, size) { fw, fh -> runOnUiThread { onFormat(fw, fh) } }
                pipeline = p
                MirrorBus.sink = p
                Log.i(TAG, "surface prête ${w}x$h, pipeline branché")
                applyTransform()
            }

            override fun onSurfaceTextureSizeChanged(st: SurfaceTexture, w: Int, h: Int) = applyTransform()

            override fun onSurfaceTextureDestroyed(st: SurfaceTexture): Boolean {
                detach()
                return true
            }

            override fun onSurfaceTextureUpdated(st: SurfaceTexture) {}
        }
        registerCloseReceiver()
    }

    private fun registerCloseReceiver() {
        val filter = IntentFilter(ReceiverService.ACTION_CLOSE_MIRROR)
        if (Build.VERSION.SDK_INT >= 33) {
            registerReceiver(closeReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            registerReceiver(closeReceiver, filter)
        }
    }

    private fun detach() {
        val p = pipeline
        if (p != null && MirrorBus.sink === p) MirrorBus.sink = null
        pipeline = null
        p?.release()
        surface?.release()
        surface = null
    }

    private fun onFormat(w: Int, h: Int) {
        videoW = w
        videoH = h
        applyTransform()
    }

    private fun applyTransform() {
        val vw = texture.width
        val vh = texture.height
        val s = DisplayTransform.scale(videoW, videoH, vw, vh, settings.displayMode)
        texture.pivotX = vw / 2f
        texture.pivotY = vh / 2f
        texture.scaleX = s.x
        texture.scaleY = s.y
    }

    private fun cycleMode() {
        val next = when (settings.displayMode) {
            DisplayMode.FILL -> DisplayMode.STRETCH
            DisplayMode.STRETCH -> DisplayMode.FIT
            DisplayMode.FIT -> DisplayMode.FILL
        }
        settings.displayMode = next
        applyTransform()
        val label = when (next) {
            DisplayMode.FILL -> "Remplir"
            DisplayMode.STRETCH -> "Étirer"
            DisplayMode.FIT -> "Ajuster"
        }
        Toast.makeText(this, label, Toast.LENGTH_SHORT).show()
        Log.i(TAG, "mode d'affichage : $next")
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_MENU -> {
                if (event.repeatCount == 0) cycleMode()
                return true
            }
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = (
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                    View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                    View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                )
        }
    }

    override fun onDestroy() {
        try { unregisterReceiver(closeReceiver) } catch (_: Exception) {}
        detach()
        super.onDestroy()
    }

    private companion object {
        const val TAG = "KairoCast"
    }
}
