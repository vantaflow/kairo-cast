package com.vantaflow.kairo.cast

/** Source d'une session de recopie ; « la dernière arrivée l'emporte ». */
enum class MirrorSource { NONE, AIRPLAY, LINK }

/** Ce qu'il faut faire du pipeline quand une source prend la main. */
enum class PipelineAction { START_FRESH, SWITCH_SOURCE }

object SessionArbiter {
    /**
     * Pipeline neuf à chaque ouverture (comportement historique d'AirPlay), sauf lors d'un
     * changement de source sur une session ouverte : le pipeline est gardé (la surface de
     * l'activité reste branchée) et repart à neuf via onSourceChanged.
     */
    fun action(prev: MirrorSource, next: MirrorSource, hasPipeline: Boolean): PipelineAction =
        if (!hasPipeline || prev == next || prev == MirrorSource.NONE) PipelineAction.START_FRESH
        else PipelineAction.SWITCH_SOURCE
}
