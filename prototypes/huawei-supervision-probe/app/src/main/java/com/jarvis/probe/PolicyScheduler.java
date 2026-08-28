package com.jarvis.probe;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.os.Build;

final class PolicyScheduler {
    private static final int REQUEST_CODE = 4311;

    private PolicyScheduler() {
    }

    static void scheduleExpiry(Context context, long endEpoch) {
        AlarmManager manager = (AlarmManager) context.getSystemService(Context.ALARM_SERVICE);
        PendingIntent pending = expiryIntent(context);
        boolean exact = Build.VERSION.SDK_INT < 31 || manager.canScheduleExactAlarms();
        try {
            if (exact) {
                manager.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, endEpoch, pending);
            } else {
                manager.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, endEpoch, pending);
            }
            long now = System.currentTimeMillis();
            ProbeLog.event(context, "expiry_scheduled", "alarm", null, now, now, 0,
                    "endEpoch=" + endEpoch + ",exact=" + exact);
        } catch (SecurityException exception) {
            long now = System.currentTimeMillis();
            ProbeLog.event(context, "availability", "alarm", null, now, now, 0,
                    "unavailable:exact-alarm-security-exception");
        }
    }

    static void cancelExpiry(Context context) {
        AlarmManager manager = (AlarmManager) context.getSystemService(Context.ALARM_SERVICE);
        manager.cancel(expiryIntent(context));
    }

    private static PendingIntent expiryIntent(Context context) {
        Intent intent = new Intent(context, PolicyExpiryReceiver.class);
        intent.setAction("com.jarvis.probe.POLICY_EXPIRED");
        return PendingIntent.getBroadcast(
                context,
                REQUEST_CODE,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE
        );
    }
}
