package com.vantaflow.kairo.cast

import android.graphics.SurfaceTexture
import android.opengl.EGL14
import android.opengl.EGLConfig
import android.opengl.EGLContext
import android.opengl.EGLDisplay
import android.opengl.EGLSurface
import android.opengl.GLES20
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.view.Surface
import java.util.concurrent.CountDownLatch

/**
 * Surface « poubelle » avec un vrai consommateur (contexte EGL pbuffer + updateTexImage) :
 * le décodeur continue de tourner quand l'activité n'a plus de surface.
 */
class PlaceholderSurface {
    private val thread = HandlerThread("kairo-placeholder").apply { start() }
    private val handler = Handler(thread.looper)
    private var display: EGLDisplay = EGL14.EGL_NO_DISPLAY
    private var context: EGLContext = EGL14.EGL_NO_CONTEXT
    private var eglSurface: EGLSurface = EGL14.EGL_NO_SURFACE
    private var texture: SurfaceTexture? = null
    @Volatile var surface: Surface? = null
        private set

    init {
        val ready = CountDownLatch(1)
        handler.post {
            try {
                setup()
            } catch (e: Exception) {
                Log.e(TAG, "surface de remplacement impossible", e)
            }
            ready.countDown()
        }
        ready.await()
    }

    private fun setup() {
        display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY)
        val v = IntArray(2)
        EGL14.eglInitialize(display, v, 0, v, 1)
        val cfgAttr = intArrayOf(
            EGL14.EGL_RED_SIZE, 8, EGL14.EGL_GREEN_SIZE, 8, EGL14.EGL_BLUE_SIZE, 8,
            EGL14.EGL_SURFACE_TYPE, EGL14.EGL_PBUFFER_BIT, EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT,
            EGL14.EGL_NONE,
        )
        val cfgs = arrayOfNulls<EGLConfig>(1)
        val n = IntArray(1)
        EGL14.eglChooseConfig(display, cfgAttr, 0, cfgs, 0, 1, n, 0)
        context = EGL14.eglCreateContext(
            display, cfgs[0], EGL14.EGL_NO_CONTEXT, intArrayOf(EGL14.EGL_CONTEXT_CLIENT_VERSION, 2, EGL14.EGL_NONE), 0,
        )
        eglSurface = EGL14.eglCreatePbufferSurface(
            display, cfgs[0], intArrayOf(EGL14.EGL_WIDTH, 1, EGL14.EGL_HEIGHT, 1, EGL14.EGL_NONE), 0,
        )
        EGL14.eglMakeCurrent(display, eglSurface, eglSurface, context)
        val tex = IntArray(1)
        GLES20.glGenTextures(1, tex, 0)
        val st = SurfaceTexture(tex[0])
        st.setDefaultBufferSize(1920, 1080)
        st.setOnFrameAvailableListener({ t ->
            try { t.updateTexImage() } catch (_: Exception) {}
        }, handler)
        texture = st
        surface = Surface(st)
    }

    fun release() {
        handler.post {
            try {
                surface?.release()
                texture?.release()
                if (display != EGL14.EGL_NO_DISPLAY) {
                    EGL14.eglMakeCurrent(display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT)
                    EGL14.eglDestroySurface(display, eglSurface)
                    EGL14.eglDestroyContext(display, context)
                    EGL14.eglTerminate(display)
                }
            } catch (e: Exception) {
                Log.w(TAG, "libération de la surface de remplacement", e)
            }
            thread.quitSafely()
        }
    }

    private companion object {
        const val TAG = "KairoCast"
    }
}
