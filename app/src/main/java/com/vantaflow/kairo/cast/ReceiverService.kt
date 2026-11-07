package com.vantaflow.kairo.cast

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import com.vantaflow.kairo.cast.link.LinkListener
import java.io.File
import java.util.concurrent.Executors

class ReceiverService : Service() {
    private val main = Handler(Looper.getMainLooper())
    // Tout le cycle de vie (démarrage, arrêt, annonces) passe par ce thread : jamais depuis un rappel natif.
    private val worker = Executors.newSingleThreadExecutor { Thread(it, "kairo-receiver") }
    private val controllers = HashMap<Int, SlotController>() // worker + lectures sous verrou
    private var supervisor: SlotSupervisor? = null // worker uniquement
    private var policy: SlotPolicy? = null
    private var policyKey: Pair<Int, StreamQuality>? = null
    private val audio = MirrorAudio.selector
    private val activeLock = Any()
    private var lastActive: List<Int> = emptyList()
    private var destroyed = false

    init {
        // Le focus de l'écran de recopie déplace le son : on applique hors du thread principal.
        audio.listener = {
            try {
                worker.execute { if (!destroyed) { applyAudio(); refreshUi() } }
            } catch (_: java.util.concurrent.RejectedExecutionException) {}
        }
    }

    private val host = object : SlotHost {
        private val advertisers = HashMap<Int, MdnsAdvertiser>()
        override val native: SlotNative = RealSlotNative
        override fun keyFile(slot: Int) = File(filesDir, SlotIdentity.keyFileName(slot)).path
        override fun pipeline(slot: Int): PipelineFeed? = MirrorBus.pipeline(slot)
        override fun startPipeline(slot: Int, audioEnabled: Boolean, onKeyFrameNeeded: () -> Unit) =
            MirrorBus.start(slot, audioEnabled, onKeyFrameNeeded)
        override fun stopPipeline(slot: Int) = MirrorBus.stop(slot)
        override fun newLink(port: Int, listener: LinkListener): LinkEndpoint = RealLinkEndpoint(port, listener)
        override fun advertise(
            slot: Int, name: String, raopName: String, port: Int,
            airplayTxt: Map<String, String>, raopTxt: Map<String, String>, linkPort: Int,
        ): () -> Unit {
            val adv = MdnsAdvertiser(this@ReceiverService)
            adv.register(name, raopName, port, airplayTxt, raopTxt, linkPort, slot)
            synchronized(advertisers) { advertisers[slot] = adv }
            return { synchronized(advertisers) { advertisers.remove(slot) }?.unregister() }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        enterForeground()
        try {
            worker.execute { reconcile() }
        } catch (e: java.util.concurrent.RejectedExecutionException) {
            Log.w(TAG, "service en arrêt, commande ignorée")
        }
        return START_STICKY
    }

    private fun notification(text: String): Notification =
        Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setOngoing(true)
            .build()

    private fun enterForeground() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Récepteur AirPlay", NotificationManager.IMPORTANCE_LOW)
        )
        startForeground(
            NOTIF_ID, notification("Démarrage…"), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
        )
    }

    private fun setNotificationText(text: String) {
        getSystemService(NotificationManager::class.java).notify(NOTIF_ID, notification(text))
    }

    /** Ce qui impose le redémarrage d'un emplacement : seule sa clé qui change le redémarre. */
    private fun keysFor(settings: CastSettings): List<Any> {
        val n = settings.slotCount
        return (0 until n).map { listOf(settings.slotName(it), n, if (n == 1) settings.quality else null, settings.audioEnabled) }
    }

    /** Worker : amène les emplacements en marche à l'état voulu par les réglages (démarrage, changement de nom ou de nombre). */
    private fun reconcile() {
        if (destroyed) return
        val settings = CastSettings(PrefsStore(this))
        val count = settings.slotCount
        val pk = count to settings.quality
        if (supervisor == null) AudioPlayer.logAvailableDecoders()
        val sup = supervisor ?: SlotSupervisor { slot -> controllerFor(slot, settings) }.also { supervisor = it }
        if (policy == null || (policyKey?.first != count) || (count == 1 && policyKey != pk)) {
            policy = SlotPolicy(count, settings.quality)
            policyKey = pk
        }
        val names = settings.slotNames
        if (sup.slots.isEmpty()) SlotsState.reset(count, names)
        else for (slot in (0 until count)) if (slot !in sup.slots) SlotsState.update(SlotState(slot, names[slot], SlotPhase.STOPPED))
        val before = sup.slots
        sup.apply(keysFor(settings))
        for (slot in before.filter { it >= count }) SlotsState.remove(slot)
        refreshUi()
    }

    private fun controllerFor(slot: Int, settings: CastSettings): SlotRunner {
        val c = SlotController(slot, host, settings, policy!!, ::onSlotChanged, ::onSlotsFreed)
        synchronized(controllers) { controllers[slot] = c }
        return object : SlotRunner {
            override fun start() = c.start()
            override fun stop() {
                c.stop()
                synchronized(controllers) { if (controllers[slot] === c) controllers.remove(slot) }
            }
        }
    }

    private fun onSlotsFreed(slots: List<Int>) {
        val cs = synchronized(controllers) { slots.mapNotNull { controllers[it] } }
        cs.forEach { it.onSlotReleased() }
    }

    /** Rappel des contrôleurs (thread quelconque) : ne fait que lire l'état et poster. */
    private fun onSlotChanged() {
        val next = SlotsState.active.value
        val prev: List<Int>
        synchronized(activeLock) {
            prev = lastActive
            lastActive = next
        }
        if (prev.isEmpty() && next.isNotEmpty()) {
            main.post {
                startActivity(Intent(this, MirrorActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
        } else if (SlotsState.shouldClose(prev, next)) {
            main.post { sendBroadcast(Intent(ACTION_CLOSE_MIRROR).setPackage(packageName)) }
        }
        if (prev != next) audio.onActiveChanged(next)
        applyAudio()
        refreshUi()
    }

    /** Un seul emplacement est audible : les autres sont coupés (libère leur lecteur audio). */
    private fun applyAudio() {
        val sel = audio.audioSlot
        val cs = synchronized(controllers) { controllers.toMap() }
        cs.forEach { (s, c) -> c.setAudioMuted(s != sel) }
    }

    private fun refreshUi() {
        if (destroyed) return
        val slots = SlotsState.slots.value
        val sel = audio.audioSlot
        val mirroring = slots.filter { it.phase == SlotPhase.MIRRORING || it.phase == SlotPhase.SATURATED }
        val text = when {
            slots.isNotEmpty() && slots.all { it.phase == SlotPhase.ERROR } -> "Erreur : récepteur indisponible"
            mirroring.isNotEmpty() ->
                "Recopie : " + mirroring.joinToString(", ") { if (it.slot == sel) "${it.name} (son)" else it.name }
            else -> "Prêt : " + slots.filter { it.phase != SlotPhase.ERROR }.joinToString(", ") { it.name }
        }
        try { setNotificationText(text) } catch (e: Exception) { Log.w(TAG, "notification : $e") }
    }

    private fun stopAll() {
        supervisor?.stopAll()
        supervisor = null
        policy = null
        policyKey = null
        MirrorBus.stopAll()
        synchronized(controllers) { controllers.clear() }
        SlotsState.reset(0, emptyList())
    }

    override fun onDestroy() {
        destroyed = true
        audio.listener = {}
        main.removeCallbacksAndMessages(null)
        worker.execute { stopAll() }
        worker.shutdown()
        super.onDestroy()
    }

    companion object {
        const val ACTION_RESTART = "com.vantaflow.kairo.cast.RESTART"
        const val ACTION_CLOSE_MIRROR = "com.vantaflow.kairo.cast.CLOSE_MIRROR"
        private const val CHANNEL = "receiver"
        private const val NOTIF_ID = 1
        private const val TAG = "KairoCast"
    }
}
