package com.jarvis.probe;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public final class PolicyExpiryReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        long scheduledEnd = PolicyStore.endEpoch(context);
        long now = System.currentTimeMillis();
        if (scheduledEnd > now + 1_000L) {
            PolicyScheduler.scheduleExpiry(context, scheduledEnd);
            return;
        }
        ProbeLog.event(context, "expiry_alarm_received", "alarm", null,
                scheduledEnd, now, Math.max(0, now - scheduledEnd), null);
        PolicyStore.stop(context, "expiry-alarm");
        context.stopService(new Intent(context, SupervisionService.class));
    }
}
