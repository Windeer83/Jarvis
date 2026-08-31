package com.jarvis.probe;

import android.accessibilityservice.AccessibilityService;
import android.os.SystemClock;
import android.view.accessibility.AccessibilityEvent;

public final class WindowEventComparisonService extends AccessibilityService {
    @Override
    protected void onServiceConnected() {
        long now = System.currentTimeMillis();
        ProbeLog.event(this, "accessibility_connected", "accessibility", null, now, now, 0,
                "window-package-only;canRetrieveWindowContent=false");
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        if (event == null || event.getEventType() != AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) {
            return;
        }
        CharSequence packageName = event.getPackageName();
        long detected = System.currentTimeMillis();
        long latency = Math.max(0, SystemClock.uptimeMillis() - event.getEventTime());
        long eventEpoch = detected - latency;
        ProbeLog.event(this, "foreground", "accessibility",
                packageName == null ? null : packageName.toString(),
                eventEpoch,
                detected,
                latency,
                "TYPE_WINDOW_STATE_CHANGED;no-node-read");
    }

    @Override
    public void onInterrupt() {
        long now = System.currentTimeMillis();
        ProbeLog.event(this, "accessibility_interrupted", "accessibility", null, now, now, 0, null);
    }
}
