package com.jarvis.mobile;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public final class BootReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        if (!Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction())) return;
        PolicyStore.clearTemporaryAccessAfterBoot(context);
        MobilePolicy policy = PolicyStore.read(context);
        if (policy != null && policy.endEpoch > System.currentTimeMillis())
            PolicyScheduler.scheduleExpiry(context, policy.endEpoch);
        if (ConnectionStore.isPaired(context) || policy != null)
            context.startForegroundService(new Intent(context, MobileRuntimeService.class));
    }
}
