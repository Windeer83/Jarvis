package com.jarvis.probe;

import android.app.usage.UsageEvents;
import android.app.usage.UsageStatsManager;
import android.content.Context;

import java.util.LinkedHashSet;
import java.util.Set;

final class ForegroundDetector {
    private static final long INITIAL_LOOKBACK_MILLIS = 60 * 60_000L;
    private static final long STEADY_LOOKBACK_MILLIS = 5_000L;

    private final Context context;
    private final UsageStatsManager manager;
    private final Set<String> seen = new LinkedHashSet<>();
    private String currentPackage;
    private long lastEventEpoch;

    ForegroundDetector(Context context) {
        this.context = context.getApplicationContext();
        this.manager = (UsageStatsManager) context.getSystemService(Context.USAGE_STATS_SERVICE);
    }

    void poll() {
        long detectedEpoch = System.currentTimeMillis();
        long lookbackStart = currentPackage == null
                ? detectedEpoch - INITIAL_LOOKBACK_MILLIS
                : detectedEpoch - STEADY_LOOKBACK_MILLIS;
        long incrementalStart = lastEventEpoch == 0 ? lookbackStart : lastEventEpoch - 1;
        UsageEvents events = manager.queryEvents(Math.max(lookbackStart, incrementalStart), detectedEpoch);
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
            lastEventEpoch = Math.max(lastEventEpoch, event.getTimeStamp());
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
