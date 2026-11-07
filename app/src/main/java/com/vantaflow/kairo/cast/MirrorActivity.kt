package com.vantaflow.kairo.cast

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.KeyEvent
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.Toast

/** Écran de recopie : une tuile par emplacement actif (mosaïque adaptative), plein écran pour une seule source. */
class MirrorActivity : Activity() {
    private lateinit var settings: CastSettings
    private lateinit var root: FrameLayout
    private val tiles = HashMap<Int, MirrorTile>()
    private val ui = Handler(Looper.getMainLooper())
    private val labels = TileLabelTimer()
    private var toast: Toast? = null
    private var lastSlots: List<SlotState> = emptyList()
    private var shown: List<Int> = emptyList() // emplacements MIRRORING affichés
    private var saturatedSeen = HashSet<Int>()
    private var focused = -1
    private var zoomed: Int? = null

    private val closeReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            Log.i(TAG, "fermeture demandée (plus aucune source)")
            finish()
        }
    }

    private val tick = object : Runnable {
        override fun run() {
            MirrorAudio.selector.tick()
            sync()
            refreshDecor()
            ui.postDelayed(this, TICK_MS)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_mirror)
        settings = CastSettings(PrefsStore(this))
        root = findViewById(R.id.mirror_root)
        // Les quatre tuiles sont créées une fois ; seul leur placement change ensuite.
        for (slot in 0 until MAX_TILES) {
            val tile = MirrorTile(this, slot)
            tile.visibility = View.GONE
            tile.onFormatChanged = { relayout() }
            tile.onSurfaceReady = { bindTiles() }
            root.addView(tile, FrameLayout.LayoutParams(1, 1))
            tiles[slot] = tile
        }
        root.addOnLayoutChangeListener { _, l, t, r, b, ol, ot, orr, ob ->
            if (r - l != orr - ol || b - t != ob - ot) relayout()
        }
        registerCloseReceiver()
        ui.post(tick)
    }

    private fun registerCloseReceiver() {
        val filter = IntentFilter(ReceiverService.ACTION_CLOSE_MIRROR)
        if (Build.VERSION.SDK_INT >= 33) {
            registerReceiver(closeReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            registerReceiver(closeReceiver, filter)
        }
    }

    /** Lit l'état des emplacements ; recalcule la disposition seulement quand il change. */
    private fun sync() {
        val slots = SlotsState.slots.value
        if (slots === lastSlots) return
        lastSlots = slots
        val mirroring = slots.filter { it.phase == SlotPhase.MIRRORING }.map { it.slot }.sorted()
        val saturated = slots.filter { it.phase == SlotPhase.SATURATED }
        for (s in saturated) if (saturatedSeen.add(s.slot)) {
            toast?.cancel()
            toast = Toast.makeText(this, getString(R.string.tile_saturated, s.name), Toast.LENGTH_LONG).also { it.show() }
        }
        saturatedSeen.retainAll(saturated.map { it.slot }.toSet())
        for (s in mirroring) if (s !in shown) labels.touch(s)
        for (s in shown) if (s !in mirroring) labels.forget(s)
        shown = mirroring
        if (zoomed != null && zoomed !in mirroring) zoomed = null
        if (focused !in mirroring) {
            focused = mirroring.firstOrNull() ?: -1
            if (focused >= 0) labels.touch(focused)
        }
        for ((slot, tile) in tiles) {
            if (slot !in mirroring) {
                tile.unbind()
                tile.alpha = 1f
                tile.visibility = View.GONE
            } else {
                tile.setName(slots.first { it.slot == slot }.name)
            }
        }
        bindTiles()
        relayout()
    }

    private fun bindTiles() {
        for (s in shown) tiles[s]?.let { if (it.hasSurface) it.bind(MirrorBus.pipeline(s)) }
    }

    private fun stateOf(slot: Int) = lastSlots.firstOrNull { it.slot == slot }

    private fun ratioOf(slot: Int): Float {
        val t = tiles[slot]?.contentRatio() ?: 0f
        if (t > 0f) return t
        val st = stateOf(slot)
        return if (st != null && st.width > 0 && st.height > 0) st.width.toFloat() / st.height else TileLayout.DEFAULT_RATIO
    }

    private var rects: Map<Int, TileRect> = emptyMap()

    private fun relayout() {
        val w = root.width
        val h = root.height
        if (w <= 0 || h <= 0 || shown.isEmpty()) return
        val single = shown.size == 1
        val z = zoomed
        // Mosaïque normale (sans zoom) : le focus se déplace sur ces positions.
        rects = TileLayout.compute(shown.associateWith { ratioOf(it) }, w, h, null)
        z?.let { tiles[it]?.bringToFront() }
        for (s in shown) {
            val tile = tiles[s] ?: continue
            tile.visibility = View.VISIBLE
            // Une tuile masquée par le zoom reste attachée (sa surface vit) mais transparente.
            tile.alpha = if (z != null && s != z) 0f else 1f
            val r = if (single || s == z) TileRect(0, 0, w, h) else rects[s] ?: continue
            tile.place(r.left, r.top, r.width, r.height)
            // Source seule ou zoomée : réglage de l'utilisateur ; mosaïque : Ajuster dans la tuile.
            tile.setDisplayMode(if (single || s == z) settings.slotDisplayMode(s) else DisplayMode.FIT)
        }
        refreshDecor()
    }

    private fun refreshDecor() {
        val several = shown.size >= 2
        val audio = MirrorAudio.selector.audioSlot
        for (s in shown) {
            val tile = tiles[s] ?: continue
            tile.setFocusedOutline(several && s == focused && zoomed == null)
            tile.setSpeaker(several && s == audio)
            tile.setLabelAlpha(if (several) labels.alpha(s) else 0f)
        }
    }

    private fun setFocus(slot: Int) {
        if (slot == focused) return
        focused = slot
        labels.touch(slot)
        MirrorAudio.selector.onFocus(slot)
        refreshDecor()
    }

    private fun cycleMode() {
        val slot = zoomed ?: shown.firstOrNull() ?: return
        val next = when (settings.slotDisplayMode(slot)) {
            DisplayMode.FILL -> DisplayMode.STRETCH
            DisplayMode.STRETCH -> DisplayMode.FIT
            DisplayMode.FIT -> DisplayMode.FILL
        }
        settings.setSlotDisplayMode(slot, next)
        relayout()
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
        when (val a = MirrorKeys.map(keyCode, event.repeatCount, shown.size)) {
            is MirrorKeyAction.MoveFocus -> {
                if (zoomed == null && focused >= 0 && rects.isNotEmpty()) setFocus(TileLayout.nextFocus(rects, focused, a.dx, a.dy))
                return true
            }
            MirrorKeyAction.CycleMode -> { cycleMode(); return true }
            MirrorKeyAction.ToggleZoom -> {
                zoomed = if (zoomed == null) focused.takeIf { it >= 0 } else null
                if (zoomed != null) labels.touch(zoomed!!)
                relayout()
                return true
            }
            MirrorKeyAction.None -> {}
        }
        return super.onKeyDown(keyCode, event)
    }

    /** Retour : sort d'abord du zoom, puis ferme l'écran. */
    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (zoomed != null) {
            zoomed = null
            relayout()
        } else {
            @Suppress("DEPRECATION")
            super.onBackPressed()
        }
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
        ui.removeCallbacksAndMessages(null)
        try { unregisterReceiver(closeReceiver) } catch (_: Exception) {}
        tiles.values.forEach { it.unbind() }
        super.onDestroy()
    }

    private companion object {
        const val TAG = "KairoCast"
        const val MAX_TILES = 4
        const val TICK_MS = 100L
    }
}
