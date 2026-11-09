package com.vantaflow.kairo.cast

/**
 * Qui a l'écran : AirPlay (Mac) ou Kairo Link (PC), jamais les deux. Une connexion AirPlay
 * ouverte pendant une session Link est mémorisée et reprend la main à la fin de celle-ci (le
 * serveur natif ne renvoie pas d'ouverture tant que le Mac reste connecté).
 */
class SourceArbiter {
    enum class Source { NONE, AIRPLAY, LINK }

    private var airplayConnected = false

    @Volatile var current = Source.NONE
        private set

    /** Réserve l'écran pour une session Link ; faux si une recopie est en cours. */
    @Synchronized fun tryClaimLink(): Boolean {
        if (current != Source.NONE) return false
        current = Source.LINK
        return true
    }

    /** Vrai s'il faut démarrer la session AirPlay maintenant. */
    @Synchronized fun airplayOpened(): Boolean {
        airplayConnected = true
        if (current == Source.LINK) return false
        val start = current == Source.NONE
        current = Source.AIRPLAY
        return start
    }

    /** Vrai s'il faut arrêter la session (AirPlay l'occupait). */
    @Synchronized fun airplayClosed(): Boolean {
        airplayConnected = false
        if (current != Source.AIRPLAY) return false
        current = Source.NONE
        return true
    }

    /** Fin de la session Link ; vrai si une connexion AirPlay attend et doit démarrer. */
    @Synchronized fun linkClosed(): Boolean {
        if (current != Source.LINK) return false
        current = if (airplayConnected) Source.AIRPLAY else Source.NONE
        return airplayConnected
    }

    /** Les données de [src] doivent-elles atteindre l'écran ? AirPlay seulement hors session Link. */
    fun accepts(src: Source): Boolean = if (src == Source.LINK) current == Source.LINK else current != Source.LINK

    @Synchronized fun reset() {
        current = Source.NONE
        airplayConnected = false
    }
}
