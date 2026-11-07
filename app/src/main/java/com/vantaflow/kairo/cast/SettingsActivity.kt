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

class SettingsActivity : Activity() {
    private lateinit var settings: CastSettings
    private lateinit var rows: LinearLayout
    private lateinit var nameValue: TextView
    private lateinit var qualityValue: TextView
    private lateinit var modeValue: TextView
    private lateinit var audioValue: TextView
    private lateinit var bootValue: TextView
    private lateinit var overlayValue: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setTheme(R.style.KairoTheme)
        setContentView(R.layout.activity_settings)
        settings = CastSettings(PrefsStore(this))
        rows = findViewById(R.id.rows)

        nameValue = row(R.string.set_name) { editName() }
        qualityValue = row(R.string.set_quality) {
            val all = StreamQuality.values()
            settings.quality = all[(all.indexOf(settings.quality) + 1) % all.size]
            refresh()
        }
        modeValue = row(R.string.set_display) {
            settings.displayMode = nextMode(settings.displayMode)
            refresh()
        }
        audioValue = row(R.string.set_audio) { settings.audioEnabled = !settings.audioEnabled; refresh() }
        bootValue = row(R.string.set_boot) { settings.startOnBoot = !settings.startOnBoot; refresh() }
        overlayValue = row(R.string.set_overlay) {
            if (!Settings.canDrawOverlays(this)) {
                startActivity(
                    Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))
                )
            }
        }
        startName = settings.name
        startQuality = settings.quality
        rows.getChildAt(0).requestFocus()
    }

    private var startName = ""
    private var startQuality = StreamQuality.QHD

    override fun onPause() {
        // Un seul redémarrage du récepteur, à la sortie, si le nom ou la netteté ont changé.
        if (settings.name != startName || settings.quality != startQuality) {
            startName = settings.name
            startQuality = settings.quality
            restartReceiver()
        }
        super.onPause()
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    private fun nextMode(cur: DisplayMode): DisplayMode {
        val order = listOf(DisplayMode.STRETCH, DisplayMode.FILL, DisplayMode.FIT)
        return order[(order.indexOf(cur) + 1) % order.size]
    }

    private fun refresh() {
        nameValue.text = settings.name
        qualityValue.text = when (settings.quality) {
            StreamQuality.HD -> "1080p"
            StreamQuality.QHD -> "1440p"
            StreamQuality.UHD -> "4K"
        }
        modeValue.text = when (settings.displayMode) {
            DisplayMode.STRETCH -> getString(R.string.mode_stretch)
            DisplayMode.FILL -> getString(R.string.mode_fill)
            DisplayMode.FIT -> getString(R.string.mode_fit)
        }
        audioValue.text = getString(if (settings.audioEnabled) R.string.on else R.string.off)
        bootValue.text = getString(if (settings.startOnBoot) R.string.on else R.string.off)
        overlayValue.text = getString(if (Settings.canDrawOverlays(this)) R.string.overlay_ok else R.string.overlay_no)
    }

    private fun restartReceiver() {
        startForegroundService(
            Intent(this, ReceiverService::class.java).setAction(ReceiverService.ACTION_RESTART)
        )
    }

    private fun editName() {
        val input = EditText(this).apply {
            setText(settings.name)
            inputType = InputType.TYPE_CLASS_TEXT
            textSize = 24f
            setSelectAllOnFocus(true)
        }
        AlertDialog.Builder(this)
            .setTitle(R.string.set_name)
            .setView(input)
            .setPositiveButton(getString(R.string.save)) { _, _ ->
                val newName = ReceiverName.normalize(input.text.toString())
                if (newName != settings.name) settings.name = newName
                refresh()
            }
            .setNegativeButton(getString(R.string.cancel), null)
            .show()
    }

    /** Ligne focalisable : libellé à gauche, valeur à droite. Retourne la vue de la valeur. */
    private fun row(label: Int, onClick: () -> Unit): TextView {
        val dp = resources.displayMetrics.density
        val value = TextView(this).apply {
            textSize = 22f
            setTextColor(getColor(R.color.kairo_muted))
            gravity = Gravity.END
        }
        val line = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            isFocusable = true
            isClickable = true
            setBackgroundResource(R.drawable.focus_row)
            setPadding((24 * dp).toInt(), (10 * dp).toInt(), (24 * dp).toInt(), (10 * dp).toInt())
            minimumHeight = (56 * dp).toInt()
            setOnClickListener { onClick() }
            addView(TextView(context).apply {
                setText(label)
                textSize = 22f
                setTypeface(typeface, Typeface.BOLD)
                setTextColor(getColor(R.color.kairo_text))
            }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(value, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        }
        rows.addView(line, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
            bottomMargin = (6 * dp).toInt()
        })
        return value
    }
}
