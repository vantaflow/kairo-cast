package com.vantaflow.kairo.cast.debug

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.vantaflow.kairo.cast.CastSettings
import com.vantaflow.kairo.cast.PrefsStore
import com.vantaflow.kairo.cast.ReceiverService

/**
 * Debug uniquement : règle le nombre d'emplacements (et au besoin le nom d'un emplacement) par adb puis relance le service.
 * adb shell am broadcast -n com.vantaflow.kairo.cast/.debug.DebugSettingsReceiver --ei slots 2 [--ei slot 1 --es name Bureau]
 */
class DebugSettingsReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        val s = CastSettings(PrefsStore(ctx))
        if (intent.hasExtra("slots")) s.slotCount = intent.getIntExtra("slots", 1)
        val name = intent.getStringExtra("name")
        if (name != null) s.setSlotName(intent.getIntExtra("slot", 0), name)
        ctx.startForegroundService(Intent(ctx, ReceiverService::class.java).setAction(ReceiverService.ACTION_RESTART))
    }
}
