package com.vantaflow.kairo.cast

import android.content.Context
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.SurfaceTexture
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.TextView

/** Une tuile : image de l'emplacement, étiquette (nom), icône de son et liseré de focus. */
class MirrorTile(context: Context, val slot: Int) : FrameLayout(context) {
    private val texture = TextureView(context)
    private val outline = View(context)
    private val label = TextView(context)
    private val speaker = ImageView(context)

    private var surface: Surface? = null
    private var attached: MirrorPipeline? = null
    private var videoW = 0
    private var videoH = 0
    private var content = IntArray(4)
    private var mode = DisplayMode.FIT

    /** Appelé (thread principal) quand le format de la source change. */
    var onFormatChanged: () -> Unit = {}

    private val formatListener: (IntArray) -> Unit = { f ->
        post {
            videoW = f[0]; videoH = f[1]
            content = intArrayOf(f[2], f[3], f[4], f[5])
            applyTransform()
            onFormatChanged()
        }
    }

    init {
        addView(texture, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        outline.background = GradientDrawable().apply {
            setColor(Color.TRANSPARENT)
            setStroke(FOCUS_PX, context.getColor(R.color.tile_focus))
        }
        outline.visibility = View.GONE
        addView(outline, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        val d = resources.displayMetrics.density
        label.setTextColor(Color.WHITE)
        label.textSize = 18f
        label.setBackgroundColor(context.getColor(R.color.tile_label_bg))
        label.setPadding((12 * d).toInt(), (6 * d).toInt(), (12 * d).toInt(), (6 * d).toInt())
        label.alpha = 0f
        addView(label, LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.START).apply {
            leftMargin = FOCUS_PX; topMargin = FOCUS_PX
        })
        speaker.setImageResource(R.drawable.ic_speaker)
        speaker.setBackgroundColor(context.getColor(R.color.tile_label_bg))
        speaker.setPadding((4 * d).toInt(), (4 * d).toInt(), (4 * d).toInt(), (4 * d).toInt())
        speaker.visibility = View.GONE
        addView(speaker, LayoutParams((36 * d).toInt(), (36 * d).toInt(), Gravity.TOP or Gravity.END).apply {
            rightMargin = FOCUS_PX; topMargin = FOCUS_PX
        })
        texture.addOnLayoutChangeListener { _, l, t, r, b, ol, ot, orr, ob ->
            if (r - l != orr - ol || b - t != ob - ot) applyTransform()
        }
        texture.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(st: SurfaceTexture, w: Int, h: Int) {
                surface = Surface(st)
                onSurfaceReady()
            }
            override fun onSurfaceTextureSizeChanged(st: SurfaceTexture, w: Int, h: Int) = applyTransform()
            override fun onSurfaceTextureDestroyed(st: SurfaceTexture): Boolean {
                unbind()
                surface?.release()
                surface = null
                return true
            }
            override fun onSurfaceTextureUpdated(st: SurfaceTexture) {}
        }
    }

    /** Appelé quand la surface devient disponible (le pipeline à brancher est choisi par l'activité). */
    var onSurfaceReady: () -> Unit = {}

    /** Branche (ou rebranche) le pipeline de l'emplacement ; [p] = null le débranche. */
    fun bind(p: MirrorPipeline?) {
        val s = surface ?: return
        if (p === attached) return
        unbind()
        if (p != null) {
            attached = p
            p.attach(s, formatListener)
        }
    }

    fun unbind() {
        attached?.detach(formatListener)
        attached = null
    }

    val hasSurface: Boolean get() = surface != null

    /** Rapport largeur/hauteur du contenu utile (0 si inconnu). */
    fun contentRatio(): Float {
        val cw = content[2]; val ch = content[3]
        val valid = cw > 0 && ch > 0 && content[0] + cw <= videoW && content[1] + ch <= videoH
        return when {
            valid -> cw.toFloat() / ch
            videoW > 0 && videoH > 0 -> videoW.toFloat() / videoH
            else -> 0f
        }
    }

    fun setDisplayMode(m: DisplayMode) {
        mode = m
        applyTransform()
    }

    private fun applyTransform() {
        val v = DisplayTransform.matrixValues(
            videoW, videoH, content[0], content[1], content[2], content[3], texture.width, texture.height, mode,
        )
        texture.setTransform(Matrix().apply { setValues(v) })
    }

    fun place(left: Int, top: Int, width: Int, height: Int) {
        val lp = (layoutParams as? FrameLayout.LayoutParams) ?: FrameLayout.LayoutParams(width, height)
        if (lp.width != width || lp.height != height || lp.leftMargin != left || lp.topMargin != top) {
            lp.width = width; lp.height = height; lp.leftMargin = left; lp.topMargin = top
            lp.gravity = Gravity.TOP or Gravity.START
            layoutParams = lp
        }
    }

    fun setName(name: String) { if (label.text.toString() != name) label.text = name }

    /** Opacité de l'étiquette (0 = masquée) ; le fondu est calculé par [TileLabelTimer.alpha]. */
    fun setLabelAlpha(a: Float) { if (label.alpha != a) label.alpha = a }

    fun setFocusedOutline(on: Boolean) { outline.visibility = if (on) View.VISIBLE else View.GONE }

    fun setSpeaker(on: Boolean) { speaker.visibility = if (on) View.VISIBLE else View.GONE }

    private companion object {
        const val FOCUS_PX = 3
    }
}
