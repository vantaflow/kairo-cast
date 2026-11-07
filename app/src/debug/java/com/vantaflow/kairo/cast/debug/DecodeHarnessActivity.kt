package com.vantaflow.kairo.cast.debug

import android.app.Activity
import android.graphics.SurfaceTexture
import android.os.Bundle
import android.os.Debug
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Gravity
import android.view.Surface
import android.view.TextureView
import android.view.WindowManager
import android.widget.LinearLayout
import com.vantaflow.kairo.cast.Frame
import com.vantaflow.kairo.cast.VideoDecoder
import java.io.File
import java.util.concurrent.atomic.AtomicInteger

/**
 * Harnais debug (spike S1) : décode N flux H.264 en parallèle sur N TextureView.
 * Extras : n (2-4), file (dans getExternalFilesDir), seconds (défaut 120), w/h (taille vidéo, défaut 1920x1080).
 */
class DecodeHarnessActivity : Activity() {
    private val tag = "KairoS1"
    private val main = Handler(Looper.getMainLooper())
    private var n = 2
    private var seconds = 120
    private var vw = 1920
    private var vh = 1080
    private lateinit var aus: List<ByteArray>
    private val decoders = arrayOfNulls<VideoDecoder>(4)
    private val feeders = arrayOfNulls<Thread>(4)
    private val updates = Array(4) { AtomicInteger() }
    private val lastCounts = IntArray(4)
    private val minFps = DoubleArray(4) { Double.MAX_VALUE }
    private var minCma = Long.MAX_VALUE
    private var minAvail = Long.MAX_VALUE
    private var maxPssKb = 0
    private var tick = 0
    @Volatile private var running = true
    private var ready = 0

    override fun onCreate(b: Bundle?) {
        super.onCreate(b)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        n = intent.getIntExtra("n", 2).coerceIn(1, 4)
        seconds = intent.getIntExtra("seconds", 120)
        vw = intent.getIntExtra("w", 1920)
        vh = intent.getIntExtra("h", 1080)
        val name = intent.getStringExtra("file") ?: "s1.h264"
        val ext = File(getExternalFilesDir(null), name)
        val data = (if (ext.exists()) ext else File(filesDir, name)).readBytes() // repli : stockage interne (run-as)
        aus = AnnexB.splitAccessUnits(data).map { data.copyOfRange(it.first, it.last + 1) }
        Log.i(tag, "S1 start n=$n units=${aus.size} ${vw}x$vh seconds=$seconds")

        val cols = 2
        val rows = if (n <= 2) 1 else 2
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        var idx = 0
        for (r in 0 until rows) {
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
            for (c in 0 until cols) {
                if (idx >= n) break
                val i = idx++
                val tv = TextureView(this)
                tv.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
                    override fun onSurfaceTextureAvailable(st: SurfaceTexture, w: Int, h: Int) = startTile(i, st)
                    override fun onSurfaceTextureSizeChanged(st: SurfaceTexture, w: Int, h: Int) {}
                    override fun onSurfaceTextureDestroyed(st: SurfaceTexture) = true
                    override fun onSurfaceTextureUpdated(st: SurfaceTexture) { updates[i].incrementAndGet() }
                }
                row.addView(tv, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f))
            }
            root.addView(row, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f))
        }
        setContentView(root)
    }

    private fun startTile(i: Int, st: SurfaceTexture) {
        val dec = VideoDecoder(Surface(st))
        dec.setVideoSize(vw, vh)
        decoders[i] = dec
        feeders[i] = Thread({
            var pts = 0L
            var k = 0
            val start = System.nanoTime()
            var sent = 0L
            while (running) {
                val d = aus[k]
                dec.feed(Frame(d, d.size, pts, (d.size > 4 && (d[4].toInt() and 0x1F).let { it == 7 || it == 5 })))
                pts += 33_333
                sent++
                k = (k + 1) % aus.size
                val wait = start + sent * 33_333_333L - System.nanoTime()
                if (wait > 0) try { Thread.sleep(wait / 1_000_000, (wait % 1_000_000).toInt()) } catch (_: InterruptedException) { break }
            }
        }, "s1-feed-$i").also { it.start() }
        main.post { if (++ready == n) main.postDelayed(::sample, 1000) }
    }

    private fun meminfo(key: String): Long = try {
        File("/proc/meminfo").readLines().firstOrNull { it.startsWith("$key:") }
            ?.substringAfter(":")?.trim()?.split(" ")?.first()?.toLong() ?: -1
    } catch (_: Exception) { -1 }

    private fun sample() {
        if (!running) return
        tick++
        val cma = meminfo("CmaFree")
        val avail = meminfo("MemAvailable")
        val mi = Debug.MemoryInfo(); Debug.getMemoryInfo(mi)
        if (cma >= 0) minCma = minOf(minCma, cma)
        if (avail >= 0) minAvail = minOf(minAvail, avail)
        maxPssKb = maxOf(maxPssKb, mi.totalPss)
        for (i in 0 until n) {
            val c = updates[i].get()
            val fps = (c - lastCounts[i]).toDouble()
            lastCounts[i] = c
            if (tick > 3) minFps[i] = minOf(minFps[i], fps) // 3 s de chauffe ignorées
            Log.i(tag, "S1 n=$n tile=$i fps=$fps cmaFreeKb=$cma memAvailKb=$avail pssKb=${mi.totalPss}")
        }
        if (tick >= seconds) finishRun() else main.postDelayed(::sample, 1000)
    }

    private fun finishRun() {
        running = false
        val errors = decoders.filterNotNull().sumOf { it.errorCount }
        val mf = (0 until n).minOf { minFps[it] }
        Log.i(tag, "S1 RESULT n=$n minFps=$mf minCmaFreeKb=$minCma minMemAvailKb=$minAvail maxPssKb=$maxPssKb errors=$errors perTileMinFps=${(0 until n).map { minFps[it] }}")
        finish()
    }

    override fun onDestroy() {
        running = false
        feeders.forEach { it?.interrupt() }
        Thread { decoders.forEach { it?.release() } }.start()
        super.onDestroy()
    }
}
