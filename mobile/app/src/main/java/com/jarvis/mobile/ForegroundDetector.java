package com.jarvis.mobile;

import android.app.usage.UsageEvents;
import android.app.usage.UsageStatsManager;
import android.content.Context;

final class ForegroundDetector {
    private static final long INITIAL_LOOKBACK = 60 * 60_000L;
    private static final long STEADY_LOOKBACK = 5_000L;
    private final UsageStatsManager manager;
    private String currentPackage;
    private long lastEventEpoch;

    ForegroundDetector(Context context) {
        manager = (UsageStatsManager) context.getSystemService(Context.USAGE_STATS_SERVICE);
    }

    String poll() {
        long now = System.currentTimeMillis();
        long start = currentPackage == null ? now - INITIAL_LOOKBACK : now - STEADY_LOOKBACK;
        if (lastEventEpoch > 0) start = Math.max(start, lastEventEpoch - 1);
        UsageEvents events = manager.queryEvents(start, now);
        if (events == null) return currentPackage;
        UsageEvents.Event event = new UsageEvents.Event();
        String latest = null;
        long latestAt = Long.MIN_VALUE;
        while (events.hasNextEvent()) {
            events.getNextEvent(event);
            int type = event.getEventType();
            if (type != UsageEvents.Event.ACTIVITY_RESUMED &&
                    type != UsageEvents.Event.MOVE_TO_FOREGROUND) continue;
            lastEventEpoch = Math.max(lastEventEpoch, event.getTimeStamp());
            if (event.getTimeStamp() >= latestAt) {
                latestAt = event.getTimeStamp();
                latest = event.getPackageName();
            }
        }
        if (latest != null) currentPackage = latest;
        return currentPackage;
    }
}
