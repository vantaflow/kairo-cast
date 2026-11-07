package com.vantaflow.kairo.cast

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Typeface
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast

class SettingsActivity : Activity() {
    private lateinit var settings: CastSettings
    private lateinit var rows: LinearLayout
    private val labels = object : SettingsRows.Labels {
        override fun sourceN(n: Int, name: String) = getString(R.string.source_n, n, name)
        override fun qualityFixed() = getString(R.string.quality_fixed)
        override fun quality(q: StreamQuality) = getString(when (q) {
            StreamQuality.HD -> R.string.quality_hd
            StreamQuality.QHD -> R.string.quality_qhd
            StreamQuality.UHD -> R.string.quality_uhd
        })
    }
    private var start = SettingsRows.Snapshot(1, emptyList(), StreamQuality.QHD)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setTheme(R.style.KairoTheme)
        setContentView(R.layout.activity_settings)
        settings = CastSettings(PrefsStore(this))
        rows = findViewById(R.id.rows)
        start = SettingsRows.snapshot(settings)
        build()
        rows.getChildAt(0).requestFocus()
    }

    override fun onPause() {
        // Un seul redémarrage du récepteur, à la sortie, si le nombre, un nom visible ou la netteté ont changé.
        val now = SettingsRows.snapshot(settings)
        if (SettingsRows.needsRestart(start, now)) {
            start = now
            restartReceiver()
        }
        super.onPause()
    }

    override fun onResume() {
        super.onResume()
        build()
    }

    /** Reconstruit toutes les lignes ; garde le focus sur la même position. */
    private fun build() {
        val focusIdx = (0 until rows.childCount).firstOrNull { rows.getChildAt(it).hasFocus() } ?: 0
        rows.removeAllViews()
        row(getString(R.string.set_count), settings.slotCount.toString()) {
            settings.slotCount = SettingsRows.nextSlotCount(settings.slotCount)
            build()
        }
        note(getString(R.string.set_count_help))
        val nameRows = SettingsRows.nameRows(settings, labels)
        if (nameRows.size == 1) {
            row(getString(R.string.set_name), nameRows[0].value) { editName(0) }
        } else {
            nameRows.forEachIndexed { i, r -> row(r.value, getString(R.string.modify)) { editName(i) } }
        }
        val q = SettingsRows.qualityRow(settings, labels)
        row(getString(R.string.set_quality), q.value, q.enabled) {
            if (!q.enabled) {
                Toast.makeText(this, R.string.quality_auto_toast, Toast.LENGTH_LONG).show()
                return@row
            }
            val all = StreamQuality.values()
            settings.quality = all[(all.indexOf(settings.quality) + 1) % all.size]
            build()
        }
        row(getString(R.string.set_display), when (settings.displayMode) {
            DisplayMode.STRETCH -> getString(R.string.mode_stretch)
            DisplayMode.FILL -> getString(R.string.mode_fill)
            DisplayMode.FIT -> getString(R.string.mode_fit)
        }) {
            settings.displayMode = nextMode(settings.displayMode)
            build()
        }
        note(getString(R.string.set_display_help))
        row(getString(R.string.set_audio), getString(if (settings.audioEnabled) R.string.on else R.string.off)) {
            settings.audioEnabled = !settings.audioEnabled; build()
        }
        row(getString(R.string.set_boot), getString(if (settings.startOnBoot) R.string.on else R.string.off)) {
            settings.startOnBoot = !settings.startOnBoot; build()
        }
        row(getString(R.string.set_overlay), getString(if (Settings.canDrawOverlays(this)) R.string.overlay_ok else R.string.overlay_no)) {
            if (!Settings.canDrawOverlays(this)) {
                startActivity(
                    Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))
                )
            }
        }
        val focusables = (0 until rows.childCount).map { rows.getChildAt(it) }.filter { it.isFocusable }
        focusables.getOrNull(focusIdx.coerceAtMost(focusables.size - 1))?.requestFocus()
    }

    private fun nextMode(cur: DisplayMode): DisplayMode {
        val order = listOf(DisplayMode.STRETCH, DisplayMode.FILL, DisplayMode.FIT)
        return order[(order.indexOf(cur) + 1) % order.size]
    }

    private fun restartReceiver() {
        startForegroundService(
            Intent(this, ReceiverService::class.java).setAction(ReceiverService.ACTION_RESTART)
        )
    }

    private fun editName(slot: Int) {
        val input = EditText(this).apply {
            setText(settings.slotName(slot))
            inputType = InputType.TYPE_CLASS_TEXT
            textSize = 24f
            setSelectAllOnFocus(true)
        }
        val dialog = AlertDialog.Builder(this)
            .setTitle(if (settings.slotCount == 1) getString(R.string.set_name) else getString(R.string.set_source_n, slot + 1))
            .setView(input)
            .setPositiveButton(getString(R.string.save), null)
            .setNegativeButton(getString(R.string.cancel), null)
            .show()
        // Le dialogue reste ouvert si le nom est déjà pris, pour pouvoir le corriger.
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            val result = settings.setSlotName(slot, input.text.toString())
            if (result == NameResult.Duplicate) {
                Toast.makeText(this, R.string.name_duplicate, Toast.LENGTH_LONG).show()
                input.error = getString(R.string.name_duplicate)
            } else {
                dialog.dismiss()
                build()
            }
        }
    }

    private fun note(text: String) {
        val dp = resources.displayMetrics.density
        rows.addView(TextView(this).apply {
            this.text = text
            textSize = 18f
            setTextColor(getColor(R.color.kairo_muted))
            setPadding((24 * dp).toInt(), 0, (24 * dp).toInt(), (6 * dp).toInt())
        })
    }

    /** Ligne focalisable : libellé à gauche, valeur à droite. */
    private fun row(label: String, valueText: String, enabled: Boolean = true, onClick: () -> Unit) {
        val dp = resources.displayMetrics.density
        val value = TextView(this).apply {
            textSize = 22f
            text = valueText
            setTextColor(getColor(R.color.kairo_muted))
            gravity = Gravity.END
        }
        val line = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            isFocusable = true
            isClickable = true
            alpha = if (enabled) 1f else 0.5f
            setBackgroundResource(R.drawable.focus_row)
            setPadding((24 * dp).toInt(), (10 * dp).toInt(), (24 * dp).toInt(), (10 * dp).toInt())
            minimumHeight = (56 * dp).toInt()
            setOnClickListener { onClick() }
            addView(TextView(context).apply {
                text = label
                textSize = 22f
                setTypeface(typeface, Typeface.BOLD)
                setTextColor(getColor(R.color.kairo_text))
            }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(value, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        }
        rows.addView(line, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
            bottomMargin = (6 * dp).toInt()
        })
    }
}
