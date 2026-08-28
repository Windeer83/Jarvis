package com.jarvis.probe;

import android.app.usage.UsageEvents;
import android.app.usage.UsageStatsManager;
import android.content.Context;

import java.util.LinkedHashSet;
import java.util.Set;

final class ForegroundDetector {
    private final Context context;
    private final UsageStatsManager manager;
    private final Set<String> seen = new LinkedHashSet<>();
    private String currentPackage;

    ForegroundDetector(Context context) {
        this.context = context.getApplicationContext();
        this.manager = (UsageStatsManager) context.getSystemService(Context.USAGE_STATS_SERVICE);
    }

    void poll() {
        long detectedEpoch = System.currentTimeMillis();
        UsageEvents events = manager.queryEvents(detectedEpoch - 5_000L, detectedEpoch);
        if (events == null) {
            return;
        }

        UsageEvents.Event event = new UsageEvents.Event();
        String latestPackage = null;
        long latestEpoch = Long.MIN_VALUE;
        while (events.hasNextEvent()) {
            events.getNextEvent(event);
            int type = event.getEventType();
            if (type != UsageEvents.Event.ACTIVITY_RESUMED
                    && type != UsageEvents.Event.MOVE_TO_FOREGROUND) {
                continue;
            }
            String fingerprint = event.getPackageName() + "|" + event.getTimeStamp() + "|" + type;
            if (!seen.contains(fingerprint)) {
                seen.add(fingerprint);
                if (seen.size() > 512) {
                    String first = seen.iterator().next();
                    seen.remove(first);
                }
                long latency = Math.max(0, detectedEpoch - event.getTimeStamp());
                ProbeLog.event(context, "foreground", "usage", event.getPackageName(),
                        event.getTimeStamp(), detectedEpoch, latency, "eventType=" + type);
            }
            if (event.getTimeStamp() >= latestEpoch) {
                latestEpoch = event.getTimeStamp();
                latestPackage = event.getPackageName();
            }
        }
        if (latestPackage != null) {
            currentPackage = latestPackage;
        }
    }

    String currentPackage() {
        return currentPackage;
    }
}
