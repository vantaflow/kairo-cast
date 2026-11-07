package com.vantaflow.kairo.cast

import android.util.Log
import com.vantaflow.kairo.cast.link.Hello
import com.vantaflow.kairo.cast.link.LinkListener
import com.vantaflow.kairo.cast.link.LinkServer

/** Couche native vue par un emplacement (remplaçable en test). */
interface SlotNative {
    fun start(
        slot: Int, name: String, deviceId: String, keyFile: String, width: Int, height: Int, maxFps: Int,
        ports: SlotPorts, listener: ReceiverListener,
    ): Boolean
    fun stop(slot: Int)
    fun setAdmit(slot: Int, allow: Boolean)
    fun airplayTxt(slot: Int): ByteArray
    fun raopTxt(slot: Int): ByteArray
    fun raopServiceName(slot: Int): String
}

object RealSlotNative : SlotNative {
    override fun start(
        slot: Int, name: String, deviceId: String, keyFile: String, width: Int, height: Int, maxFps: Int,
        ports: SlotPorts, listener: ReceiverListener,
    ) = NativeReceiver.start(slot, name, deviceId, keyFile, width, height, maxFps, ports, listener)
    override fun stop(slot: Int) = NativeReceiver.stop(slot)
    override fun setAdmit(slot: Int, allow: Boolean) = NativeReceiver.setAdmit(slot, allow)
    override fun airplayTxt(slot: Int) = NativeReceiver.airplayTxt(slot)
    override fun raopTxt(slot: Int) = NativeReceiver.raopTxt(slot)
    override fun raopServiceName(slot: Int) = NativeReceiver.raopServiceName(slot)
}

interface LinkEndpoint {
    fun start()
    fun stop()
    fun closeClient()
    fun requestKeyFrame()
}

/** Tout ce qu'un [SlotController] touche hors de la logique pure : natif, pipelines, Link, mDNS. */
interface SlotHost {
    val native: SlotNative
    fun keyFile(slot: Int): String
    fun pipeline(slot: Int): PipelineFeed?
    fun startPipeline(slot: Int, audioEnabled: Boolean, onKeyFrameNeeded: () -> Unit)
    fun stopPipeline(slot: Int)
    fun newLink(port: Int, listener: LinkListener): LinkEndpoint
    /** Annonce mDNS de l'emplacement ; renvoie la fonction de retrait (bloquante). */
    fun advertise(
        slot: Int, name: String, raopName: String, port: Int,
        airplayTxt: Map<String, String>, raopTxt: Map<String, String>, linkPort: Int,
    ): () -> Unit
}

enum class SaturationAction { RUN, REFUSE_KEEP_SESSION, REFUSE_CLOSE_LINK }

/** Que faire d'une source selon la décision de [SlotPolicy] : AirPlay garde sa session (images ignorées), Link est fermé par BYE. */
fun admissionOutcome(admission: Admission, source: MirrorSource): SaturationAction = when (admission) {
    Admission.Accepted -> SaturationAction.RUN
    Admission.Saturated ->
        if (source == MirrorSource.LINK) SaturationAction.REFUSE_CLOSE_LINK else SaturationAction.REFUSE_KEEP_SESSION
}

/**
 * Un emplacement : serveur AirPlay natif, serveur Link, annonces, arbitrage Mac/PC (« dernier arrivé »)
 * et admission par [SlotPolicy]. [start]/[stop] ne sont jamais appelés depuis un rappel natif
 * (le service les enchaîne sur son thread de travail).
 */
class SlotController(
    val slot: Int,
    private val host: SlotHost,
    private val settings: CastSettings,
    private val policy: SlotPolicy,
    /** Appelé hors verrou quand la phase change (le service ouvre ou ferme l'écran, met à jour la notification). */
    private val onChanged: () -> Unit,
    /** Appelé hors verrou avec les emplacements saturés repris par la libération de budget. */
    private val onFreed: (List<Int>) -> Unit,
) : SlotRunner {
    private val arb = Any()
    @Volatile private var name = settings.slotName(slot)
    @Volatile private var accepting = false
    @Volatile private var owner = MirrorSource.NONE
    @Volatile private var airplayConnected = false
    @Volatile private var saturated = false
    @Volatile private var audioMuted = true // muet tant que le service n'a pas désigné cet emplacement
    private var ignoredLogged = false
    @Volatile private var link: LinkEndpoint? = null
    private var unadvertise: (() -> Unit)? = null
    private var started = false
    @Volatile private var width = 0
    @Volatile private var height = 0

    private fun setPhase(p: SlotPhase) {
        SlotsState.update(SlotState(slot, name, p, width, height))
    }

    private fun changed() = try { onChanged() } catch (e: Exception) { Log.w(TAG, "[$slot] onChanged : $e") }

    private fun keyFrameNeeded() {
        if (owner == MirrorSource.LINK) link?.requestKeyFrame()
    }

    override fun start(): Boolean {
        if (started) return true
        name = settings.slotName(slot)
        setPhase(SlotPhase.STOPPED)
        val ports = SlotPorts.of(slot)
        val mode = policy.announced()
        val single = policy.slotCount <= 1
        accepting = true
        host.native.setAdmit(slot, true) // l'admission n'est pas remise à zéro au redémarrage
        val ok = try {
            host.native.start(
                slot, name, settings.slotDeviceId(slot), host.keyFile(slot),
                if (single) settings.quality.width else mode.width,
                if (single) settings.quality.height else mode.height,
                if (single) 0 else mode.maxFps, ports, listener,
            )
        } catch (e: Throwable) {
            Log.e(TAG, "[$slot] démarrage natif : $e")
            false
        }
        if (!ok) return fail("serveur natif échoué")
        var linkPort = 0
        try {
            val ls = host.newLink(ports.link, linkListener)
            ls.start()
            link = ls
            linkPort = ports.link
        } catch (e: Exception) {
            Log.e(TAG, "[$slot] serveur Link indisponible (port ${ports.link}) : $e")
            // Emplacement 0 : comme avant, AirPlay continue sans Link. Les autres : erreur franche.
            if (slot != 0) {
                host.native.stop(slot)
                return fail("port Link pris")
            }
        }
        try {
            unadvertise = host.advertise(
                slot, name, host.native.raopServiceName(slot), ports.airplayHttp,
                TxtRecord.parse(host.native.airplayTxt(slot)), TxtRecord.parse(host.native.raopTxt(slot)), linkPort,
            )
        } catch (e: Exception) {
            Log.e(TAG, "[$slot] annonce mDNS : $e")
            link?.stop(); link = null
            host.native.stop(slot)
            return fail("annonce mDNS")
        }
        started = true
        setPhase(SlotPhase.WAITING)
        Log.i(TAG, "[$slot] récepteur démarré : $name")
        changed()
        return true
    }

    private fun fail(why: String): Boolean {
        Log.e(TAG, "[$slot] $why")
        accepting = false
        setPhase(SlotPhase.ERROR)
        changed()
        return false
    }

    override fun stop() {
        accepting = false
        unadvertise?.let { try { it() } catch (_: Exception) {} }
        unadvertise = null
        link?.stop()
        link = null
        if (started) host.native.stop(slot)
        started = false
        val freed: List<Int>
        synchronized(arb) {
            owner = MirrorSource.NONE
            airplayConnected = false
            saturated = false
            freed = policy.release(slot)
            host.stopPipeline(slot)
        }
        width = 0; height = 0
        setPhase(SlotPhase.STOPPED)
        if (freed.isNotEmpty()) onFreed(freed)
        changed()
    }

    fun setAudioMuted(muted: Boolean) {
        audioMuted = muted
        host.pipeline(slot)?.setAudioMuted(muted)
    }

    /** Le budget libéré par un autre emplacement profite à celui-ci : Link redemande une image clé, AirPlay attend la prochaine IDR. */
    fun onSlotReleased() = resumeFromSaturation()

    /** Admission d'un flux de [w]x[h] ; vrai si accepté, sinon l'emplacement passe en SATURATED. */
    internal fun onStreamFormat(w: Int, h: Int): Boolean = synchronized(arb) { admit(w, h) }

    /** Le décodeur matériel n'a plus de ressources : même traitement qu'une saturation par le budget. */
    internal fun onResourceError() {
        synchronized(arb) {
            if (owner == MirrorSource.NONE || saturated) return
            saturated = true
            Log.w(TAG, "[$slot] ressources de décodage insuffisantes")
            setPhase(SlotPhase.SATURATED)
            // Le décodeur bloqué est jeté ; un nouveau sera créé à la reprise.
            host.pipeline(slot)?.onVideoReset()
        }
        // Le budget de cet emplacement est rendu : un autre emplacement saturé peut reprendre.
        val freed = policy.release(slot)
        if (freed.isNotEmpty()) onFreed(freed)
        if (owner == MirrorSource.LINK) Thread({ link?.closeClient() }, "kairo-link-bye").start()
        changed()
    }

    /** Reprise après libération de budget (appelé sur n'importe quel thread, jamais de cycle de vie natif). */
    fun resumeFromSaturation() {
        synchronized(arb) {
            if (!saturated || owner == MirrorSource.NONE) return
            saturated = false
            setPhase(SlotPhase.MIRRORING)
            host.pipeline(slot)?.onVideoFormat(width, height, 0, 0, width, height)
            if (owner == MirrorSource.LINK) link?.requestKeyFrame()
            // AirPlay : on attend la prochaine image clé ou la reconnexion.
        }
        changed()
    }

    /** Prend la main (sous [arb]) ; renvoie la source précédente. */
    private fun beginSession(src: MirrorSource): MirrorSource {
        synchronized(arb) {
            val prev = owner
            owner = src
            ignoredLogged = false
            saturated = false
            val p = host.pipeline(slot)
            when (SessionArbiter.action(prev, src, p != null)) {
                PipelineAction.START_FRESH -> {
                    host.startPipeline(slot, settings.audioEnabled, ::keyFrameNeeded)
                    host.pipeline(slot)?.let {
                        it.setAudioMuted(audioMuted)
                        it.onResourceError = ::onResourceError
                    }
                }
                PipelineAction.SWITCH_SOURCE -> p?.onSourceChanged()
            }
            return prev
        }
    }

    private fun endSession(src: MirrorSource) {
        val freed: List<Int>
        synchronized(arb) {
            if (owner != src) return
            owner = MirrorSource.NONE
            saturated = false
            host.stopPipeline(slot)
            freed = policy.release(slot)
            // Le Mac est resté connecté mais AirPlay ne permet pas de redemander une image clé.
            setPhase(if (src == MirrorSource.LINK && airplayConnected) SlotPhase.MAC_WAITING else SlotPhase.WAITING)
        }
        if (freed.isNotEmpty()) onFreed(freed)
        changed()
    }

    /** Vrai si les données AirPlay doivent être traitées ; sinon (Link actif ou saturé) elles sont ignorées. */
    private fun airplayActive(): Boolean {
        if (!accepting) return false
        if (owner == MirrorSource.AIRPLAY) return !saturated
        if (owner == MirrorSource.LINK && !ignoredLogged) {
            ignoredLogged = true
            Log.i(TAG, "[$slot] AirPlay : données ignorées tant que Link est actif")
        }
        return false
    }

    /** Admission par la politique ; vrai si le flux est accepté. Sous [arb]. */
    private fun admit(w: Int, h: Int): Boolean {
        if (!accepting) return false // onVideoFormat tardif après stop()
        width = w; height = h
        return when (admissionOutcome(policy.admit(slot, w, h), owner)) {
            SaturationAction.RUN -> { saturated = false; setPhase(SlotPhase.MIRRORING); true }
            SaturationAction.REFUSE_KEEP_SESSION, SaturationAction.REFUSE_CLOSE_LINK -> {
                saturated = true
                Log.w(TAG, "[$slot] ${SlotPolicy.MESSAGE_SATURATED} (${w}x$h)")
                setPhase(SlotPhase.SATURATED)
                false
            }
        }
    }

    private val listener = object : ReceiverListener {
        override fun onConnectionOpened() {
            if (!accepting) return
            airplayConnected = true
            val prev = beginSession(MirrorSource.AIRPLAY)
            if (prev == MirrorSource.LINK) {
                Log.i(TAG, "[$slot] AirPlay arrive : la session Link est fermée")
                link?.closeClient() // hors [arb] : closeClient prend le verrou de dispatch du serveur Link
            }
        }
        override fun onConnectionClosed() {
            if (!accepting) return
            airplayConnected = false
            endSession(MirrorSource.AIRPLAY)
            val wasMacWaiting = SlotsState.slots.value.any { it.slot == slot && it.phase == SlotPhase.MAC_WAITING }
            if (wasMacWaiting) {
                setPhase(SlotPhase.WAITING)
                changed()
            }
        }
        override fun onVideoFormat(width: Int, height: Int, contentX: Int, contentY: Int, contentW: Int, contentH: Int) {
            if (!accepting) return
            if (owner != MirrorSource.AIRPLAY) {
                val prev = beginSession(MirrorSource.AIRPLAY)
                if (prev == MirrorSource.LINK) {
                    Log.i(TAG, "[$slot] AirPlay arrive : la session Link est fermée")
                    link?.closeClient()
                }
            }
            Log.i(TAG, "[$slot] onVideoFormat flux ${width}x$height contenu x=$contentX y=$contentY ${contentW}x$contentH")
            val ok = onStreamFormat(width, height)
            if (ok) host.pipeline(slot)?.onVideoFormat(width, height, contentX, contentY, contentW, contentH)
            changed()
        }
        override fun onVideoData(data: ByteArray, size: Int, ptsUs: Long) {
            if (!airplayActive()) return
            host.pipeline(slot)?.onVideoData(data, size, ptsUs)
        }
        override fun onAudioData(data: ByteArray, size: Int, ptsUs: Long, codecType: Int) {
            if (!airplayActive()) return
            host.pipeline(slot)?.onAudioData(data, size, ptsUs, codecType)
        }
        override fun onAudioVolume(volumeDb: Float) {
            if (!accepting) return
            host.pipeline(slot)?.onAudioVolume(volumeDb)
        }
        override fun onVideoReset() {
            if (!airplayActive()) return
            host.pipeline(slot)?.onVideoReset()
        }
    }

    private val linkListener = object : LinkListener {
        override fun onLinkOpened(hello: Hello) {
            if (!accepting) return
            beginSession(MirrorSource.LINK)
            Log.i(TAG, "[$slot] Link : session ouverte (${hello.name} ${hello.w}x${hello.h})")
            val ok = onStreamFormat(hello.w, hello.h)
            if (ok) host.pipeline(slot)?.onVideoFormat(hello.w, hello.h, 0, 0, hello.w, hello.h)
            else Thread({ link?.closeClient() }, "kairo-link-bye").start() // saturé : BYE hors du rappel ; onLinkClosed libère
            changed()
        }
        override fun onLinkVideo(data: ByteArray, size: Int, ptsUs: Long, key: Boolean) {
            if (owner != MirrorSource.LINK || saturated) return
            host.pipeline(slot)?.onVideoData(data, size, ptsUs)
        }
        override fun onLinkAudio(pcm: ByteArray, size: Int, ptsUs: Long) {
            if (owner != MirrorSource.LINK || saturated) return
            host.pipeline(slot)?.onPcmAudio(pcm, size, ptsUs)
        }
        override fun onLinkClosed() {
            Log.i(TAG, "[$slot] Link : session fermée")
            endSession(MirrorSource.LINK)
        }
    }

    private companion object {
        const val TAG = "KairoCast"
    }
}

/** Adaptateur [LinkServer] → [LinkEndpoint]. */
class RealLinkEndpoint(port: Int, listener: LinkListener) : LinkEndpoint {
    private val server = LinkServer(port, listener)
    override fun start() = server.start()
    override fun stop() = server.stop()
    override fun closeClient() = server.closeClient()
    override fun requestKeyFrame() { server.requestKeyFrame() }
}
