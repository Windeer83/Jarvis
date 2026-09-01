package com.jarvis.mobile;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.os.Build;

final class PolicyScheduler {
    private static final int REQUEST = 42731;
    private PolicyScheduler() { }

    static void scheduleExpiry(Context context, long endEpoch) {
        AlarmManager manager = (AlarmManager) context.getSystemService(Context.ALARM_SERVICE);
        PendingIntent intent = pending(context);
        if (Build.VERSION.SDK_INT < 31 || manager.canScheduleExactAlarms())
            manager.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, endEpoch, intent);
        else
            manager.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, endEpoch, intent);
    }

    static void cancelExpiry(Context context) {
        ((AlarmManager) context.getSystemService(Context.ALARM_SERVICE)).cancel(pending(context));
    }

    private static PendingIntent pending(Context context) {
        return PendingIntent.getBroadcast(context, REQUEST,
                new Intent(context, PolicyExpiryReceiver.class),
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }
}
