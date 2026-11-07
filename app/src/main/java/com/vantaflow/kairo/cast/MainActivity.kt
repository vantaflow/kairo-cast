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
        val name = CastSettings(PrefsStore(this)).name
        findViewById<TextView>(R.id.name).text = name
        findViewById<TextView>(R.id.ip).text = getString(R.string.ip_label, NetworkInfo.localIpv4(this) ?: getString(R.string.not_connected))
        findViewById<TextView>(R.id.instruction).text =
            getString(R.string.instruction, name)
        findViewById<TextView>(R.id.instruction_pc).text = getString(R.string.instruction_pc, name)
        collector = scope.launch {
            ReceiverState.status.collect { render(it) }
        }
        (if (resume.visibility == View.VISIBLE) resume else findViewById<Button>(R.id.settings)).requestFocus()
    }

    private fun render(s: Status) {
        when (s) {
            is Status.Mirroring -> {
                statusView.text = getString(R.string.status_mirroring, s.width, s.height)
                if (resume.visibility != View.VISIBLE) {
                    resume.visibility = View.VISIBLE
                    resume.requestFocus()
                }
            }
            Status.Waiting -> {
                statusView.text = getString(R.string.status_waiting)
                resume.visibility = View.GONE
            }
            Status.MacWaiting -> {
                statusView.text = getString(R.string.status_mac_waiting)
                resume.visibility = View.GONE
            }
            Status.Stopped -> {
                statusView.text = getString(R.string.status_stopped)
                resume.visibility = View.GONE
            }
        }
    }

    override fun onPause() {
        collector?.cancel()
        super.onPause()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
