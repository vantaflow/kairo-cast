package com.vantaflow.kairo.cast

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.view.View
import android.widget.Button
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class MainActivity : Activity() {
    private val scope = CoroutineScope(Dispatchers.Main.immediate)
    private var collector: Job? = null
    private lateinit var resume: Button
    private lateinit var statusView: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setTheme(R.style.KairoTheme)
        setContentView(R.layout.activity_main)
        resume = findViewById(R.id.resume)
        statusView = findViewById(R.id.status)
        resume.setOnClickListener { startActivity(Intent(this, MirrorActivity::class.java)) }
        findViewById<Button>(R.id.settings).setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        startForegroundService(Intent(this, ReceiverService::class.java))
    }

    override fun onResume() {
        super.onResume()
        val settings = CastSettings(PrefsStore(this))
        val names = settings.slotNames
        single = names.size == 1
        findViewById<TextView>(R.id.ip).text = getString(R.string.ip_label, NetworkInfo.localIpv4(this) ?: getString(R.string.not_connected))
        findViewById<TextView>(R.id.instruction).text = getString(if (names.size == 1) R.string.instruction_single else R.string.instruction, names.joinToString(" / "))
        findViewById<TextView>(R.id.instruction_pc).text = getString(R.string.instruction_pc, names.joinToString(" / "))
        collector = scope.launch {
            SlotsState.slots.collect { renderSlots(names, it) }
        }
        collector2 = scope.launch {
            ReceiverState.status.collect { render(it) }
        }
        (if (resume.visibility == View.VISIBLE) resume else findViewById<Button>(R.id.settings)).requestFocus()
    }

    private var collector2: Job? = null
    private var single = true

    private fun phaseLabel(s: SlotState?): String = when (s?.phase) {
        SlotPhase.MIRRORING -> getString(R.string.slot_mirroring, s.width, s.height)
        SlotPhase.MAC_WAITING -> getString(R.string.slot_mac_waiting)
        SlotPhase.SATURATED -> getString(R.string.slot_saturated)
        SlotPhase.WAITING -> getString(R.string.slot_waiting)
        else -> getString(R.string.slot_stopped)
    }

    private fun renderSlots(names: List<String>, states: List<SlotState>) {
        val box = findViewById<android.widget.LinearLayout>(R.id.slots)
        box.removeAllViews()
        val single = names.size == 1
        names.forEachIndexed { i, name ->
            val st = states.firstOrNull { it.slot == i }
            if (single) {
                box.addView(line(name, 56f, R.color.kairo_text, true))
            } else {
                box.addView(line("$name  ·  ${phaseLabel(st)}", 28f, R.color.kairo_text, true))
            }
        }
    }

    private fun line(text: String, size: Float, color: Int, bold: Boolean) = TextView(this).apply {
        this.text = text
        setTextColor(getColor(color))
        textSize = size
        if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD)
        gravity = android.view.Gravity.CENTER
    }

    private fun render(s: Status) {
        when (s) {
            is Status.Mirroring -> {
                statusView.text = if (single && s is Status.Mirroring) getString(R.string.status_mirroring, s.width, s.height) else ""
                if (resume.visibility != View.VISIBLE) {
                    resume.visibility = View.VISIBLE
                    resume.requestFocus()
                }
            }
            else -> {
                statusView.text = when {
                    s == Status.Stopped -> getString(R.string.status_stopped)
                    !single -> ""
                    s == Status.MacWaiting -> getString(R.string.status_mac_waiting)
                    else -> getString(R.string.status_waiting)
                }
                resume.visibility = View.GONE
            }
        }
    }

    override fun onPause() {
        collector?.cancel()
        collector2?.cancel()
        super.onPause()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
