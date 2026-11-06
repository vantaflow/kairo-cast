package com.vantaflow.kairo.cast

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return
        if (CastSettings(PrefsStore(context)).startOnBoot) {
            context.startForegroundService(Intent(context, ReceiverService::class.java))
        }
    }
}
