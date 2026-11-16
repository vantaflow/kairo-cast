package com.vantaflow.kairo.cast.sender

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.text.Editable
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

/** Écran principal : sources de la TV avec leur état, nom de l'appareil, son, partage en cours. */
class MainActivity : Activity() {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var finder: BoxFinder
    private lateinit var list: LinearLayout
    private lateinit var current: LinearLayout
    private lateinit var currentText: TextView
    private lateinit var nameField: EditText
    private lateinit var audioBox: CheckBox
    private var boxes = emptyList<Box>()
    private val status = mutableMapOf<String, BoxStatus?>()
    private var pending: Box? = null
    private var polling = false

    private val prefs by lazy { getSharedPreferences("envoi", MODE_PRIVATE) }
    private val onState: () -> Unit = { render() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val pad = dp(16)
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(pad, dp(24), pad, pad) }
        root.addView(text(getString(R.string.app_name), 24f, TEXT, bold = true))
        root.addView(text(getString(R.string.intro), 14f, MUTED).apply { setPadding(0, dp(6), 0, dp(16)) })

        current = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = rounded(BLUE_DARK)
            setPadding(pad, pad, pad, pad)
            visibility = View.GONE
        }
        currentText = text("", 16f, TEXT, bold = true)
        current.addView(currentText)
        current.addView(Button(this).apply {
            text = getString(R.string.stop)
            setOnClickListener { CastService.stop(this@MainActivity) }
        }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(8) })
        root.addView(current, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dp(16) })

        root.addView(text(getString(R.string.sources), 13f, MUTED, bold = true))
        list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(0, dp(8), 0, dp(16)) }
        root.addView(list)

        root.addView(text(getString(R.string.device_name), 13f, MUTED, bold = true))
        nameField = EditText(this).apply {
            setText(prefs.getString("name", null) ?: defaultName())
            setTextColor(TEXT)
            setSingleLine()
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun afterTextChanged(s: Editable?) { prefs.edit().putString("name", s.toString().trim()).apply() }
            })
        }
        root.addView(nameField)
        audioBox = CheckBox(this).apply {
            text = getString(R.string.send_audio)
            setTextColor(TEXT)
            isChecked = prefs.getBoolean("audio", true)
            setOnCheckedChangeListener { _, v -> prefs.edit().putBoolean("audio", v).apply() }
        }
        root.addView(audioBox)

        setContentView(ScrollView(this).apply { setBackgroundColor(BG); addView(root) })
        finder = BoxFinder(this) { boxes = it; render() }
    }

    override fun onStart() {
        super.onStart()
        CastState.listen(onState)
        finder.start()
        polling = true
        main.post(poll)
        render()
    }

    override fun onStop() {
        CastState.unlisten(onState)
        finder.stop()
        polling = false
        main.removeCallbacks(poll)
        super.onStop()
    }

    /** Toutes les 4 s : état de chaque source (libre / occupée par…), hors celle vers laquelle on partage. */
    private val poll = object : Runnable {
        override fun run() {
            if (!polling) return
            val targets = boxes.filter { it.id != CastState.box?.id }
            Thread {
                val r = targets.associate { it.id to BoxStatus.query(it.host, it.port) }
                main.post { status.putAll(r); render() }
            }.start()
            main.postDelayed(this, 4000)
        }
    }

    private fun render() {
        val active = CastState.box
        current.visibility = if (active != null) View.VISIBLE else View.GONE
        currentText.text = CastState.message
        list.removeAllViews()
        if (boxes.isEmpty()) list.addView(text(getString(R.string.searching), 15f, MUTED))
        for (b in boxes) {
            val here = active?.id == b.id
            val st = status[b.id]
            val state = when {
                here -> getString(R.string.state_here)
                st != null -> st.describe()
                b.id in status -> getString(R.string.state_unknown)
                else -> ""
            }
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                background = rounded(if (here) BLUE_DARK else ROW)
                setPadding(dp(16), dp(14), dp(16), dp(14))
                isClickable = !here
                if (!here) setOnClickListener { choose(b) }
            }
            row.addView(text(b.name, 17f, TEXT, bold = true))
            if (state.isNotEmpty()) row.addView(text(state, 14f, if (st?.busy == true) AMBER else MUTED))
            list.addView(row, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dp(8) })
        }
        if (active == null && CastState.message.isNotEmpty() && CastState.message != lastToast) {
            lastToast = CastState.message
            Toast.makeText(this, CastState.message, Toast.LENGTH_LONG).show()
        }
    }
    private var lastToast = ""

    private fun choose(b: Box) {
        val st = status[b.id]
        if (st?.busy == true) {
            AlertDialog.Builder(this)
                .setTitle(R.string.busy_title)
                .setMessage(getString(R.string.busy_message, b.name, st.describe()))
                .setPositiveButton(R.string.share) { _, _ -> ask(b) }
                .setNegativeButton(R.string.cancel, null)
                .show()
        } else ask(b)
    }

    /** Autorisations (micro pour le son, notifications), puis demande de capture d'écran. */
    private fun ask(b: Box) {
        pending = b
        val perms = buildList {
            if (audioBox.isChecked && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) add(Manifest.permission.RECORD_AUDIO)
            if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) add(Manifest.permission.POST_NOTIFICATIONS)
        }
        if (perms.isNotEmpty()) requestPermissions(perms.toTypedArray(), REQ_PERMS) else capture()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQ_PERMS) return
        if (audioBox.isChecked && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            Toast.makeText(this, R.string.audio_denied, Toast.LENGTH_LONG).show()
        }
        capture()
    }

    @Suppress("DEPRECATION")
    private fun capture() {
        if (pending == null) return
        startActivityForResult(getSystemService(MediaProjectionManager::class.java).createScreenCaptureIntent(), REQ_CAPTURE)
    }

    @Deprecated("API Activity")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQ_CAPTURE) return
        val b = pending ?: return
        pending = null
        if (resultCode != RESULT_OK || data == null) return
        val name = nameField.text.toString().trim().ifEmpty { defaultName() }
        if (CastState.box != null) CastService.stop(this) // un seul partage : l'ancien s'arrête
        main.postDelayed({ CastService.start(this, b, resultCode, data, name, audioBox.isChecked) }, if (CastState.box != null) 800L else 0L)
    }

    private fun defaultName(): String =
        Settings.Global.getString(contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() } ?: Build.MODEL

    private fun text(s: String, size: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = s
        textSize = size
        setTextColor(color)
        gravity = Gravity.START
        if (bold) typeface = Typeface.DEFAULT_BOLD
    }

    private fun rounded(color: Int) = GradientDrawable().apply { setColor(color); cornerRadius = dp(12).toFloat() }
    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    companion object {
        private const val REQ_PERMS = 1
        private const val REQ_CAPTURE = 2
        private val BG = Color.parseColor("#0B1220")
        private val ROW = Color.parseColor("#162033")
        private val BLUE_DARK = Color.parseColor("#1E3A8A")
        private val TEXT = Color.parseColor("#F1F5F9")
        private val MUTED = Color.parseColor("#94A3B8")
        private val AMBER = Color.parseColor("#FBBF24")
    }
}
