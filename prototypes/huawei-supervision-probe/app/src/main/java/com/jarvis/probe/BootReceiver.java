package com.jarvis.probe;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;

public final class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        // elapsedRealtime resets on reboot, so a pre-reboot five-minute override must never carry over.
        PolicyStore.clearTemporaryAccess(context);
        if (!PolicyStore.isActive(context)) {
            return;
        }
        long now = System.currentTimeMillis();
        ProbeLog.event(context, "boot_recovery_attempt", "boot", null, now, now, 0,
                intent == null ? null : intent.getAction());
        PolicyScheduler.scheduleExpiry(context, PolicyStore.endEpoch(context));
        Intent service = new Intent(context, SupervisionService.class);
        if (Build.VERSION.SDK_INT >= 26) {
            context.startForegroundService(service);
        } else {
            context.startService(service);
        }
    }
}
