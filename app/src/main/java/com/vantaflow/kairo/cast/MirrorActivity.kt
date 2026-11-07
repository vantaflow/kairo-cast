package com.vantaflow.kairo.cast

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.Matrix
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
    private var toast: Toast? = null
    private val formatListener: (IntArray) -> Unit = { f -> runOnUiThread { onFormat(f[0], f[1], f[2], f[3], f[4], f[5]) } }
    private var attached: MirrorPipeline? = null
    private var surface: Surface? = null
    private var videoW = 0
    private var videoH = 0
    private var content = IntArray(4)

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
        texture.addOnLayoutChangeListener { _, l, t, r, b, ol, ot, orr, ob ->
            if (r - l != orr - ol || b - t != ob - ot) applyTransform()
        }
        texture.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(st: SurfaceTexture, w: Int, h: Int) {
                val s = Surface(st)
                surface = s
                val p = MirrorBus.pipeline
                if (p == null) {
                    Log.w(TAG, "aucune recopie en cours : fermeture")
                    finish()
                    return
                }
                attached = p
                p.attach(s, formatListener)
                Log.i(TAG, "surface prête ${w}x$h, branchée au pipeline")
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
        attached?.detach(formatListener)
        attached = null
        surface?.release()
        surface = null
    }

    private fun onFormat(w: Int, h: Int, cx: Int, cy: Int, cw: Int, ch: Int) {
        videoW = w
        videoH = h
        content = intArrayOf(cx, cy, cw, ch)
        applyTransform()
    }

    private fun applyTransform() {
        val v = DisplayTransform.matrixValues(
            videoW, videoH, content[0], content[1], content[2], content[3],
            texture.width, texture.height, settings.displayMode,
        )
        texture.setTransform(Matrix().apply { setValues(v) })
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
        toast?.cancel()
        toast = Toast.makeText(this, label, Toast.LENGTH_SHORT).also { it.show() }
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
