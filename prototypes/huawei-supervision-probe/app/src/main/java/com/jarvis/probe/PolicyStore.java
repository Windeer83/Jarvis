package com.jarvis.probe;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.SystemClock;

import org.json.JSONException;
import org.json.JSONObject;

import java.util.UUID;

final class PolicyStore {
    private static final String FILE = "probe_policy";
    private static final String ACTIVE = "active";
    private static final String POLICY_ID = "policy_id";
    private static final String START_EPOCH = "start_epoch";
    private static final String END_EPOCH = "end_epoch";
    private static final String TEMP_PACKAGE = "temp_package";
    private static final String TEMP_UNTIL_ELAPSED = "temp_until_elapsed";

    private PolicyStore() {
    }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    static void start(Context context, long durationMillis, String source) {
        long now = System.currentTimeMillis();
        String id = "probe-" + UUID.randomUUID();
        prefs(context).edit()
                .putBoolean(ACTIVE, true)
                .putString(POLICY_ID, id)
                .putLong(START_EPOCH, now)
                .putLong(END_EPOCH, now + durationMillis)
                .remove(TEMP_PACKAGE)
                .remove(TEMP_UNTIL_ELAPSED)
                .apply();
        PolicyScheduler.scheduleExpiry(context, now + durationMillis);
        ProbeLog.event(context, "policy_started", source, null, now, now, 0,
                "id=" + id + ",durationMs=" + durationMillis);
    }

    static void stop(Context context, String reason) {
        boolean wasActive = prefs(context).getBoolean(ACTIVE, false);
        prefs(context).edit()
                .putBoolean(ACTIVE, false)
                .remove(TEMP_PACKAGE)
                .remove(TEMP_UNTIL_ELAPSED)
                .apply();
        PolicyScheduler.cancelExpiry(context);
        if (wasActive) {
            long now = System.currentTimeMillis();
            ProbeLog.event(context, "policy_stopped", "local", null, now, now, 0, reason);
        }
    }

    static boolean isActive(Context context) {
        SharedPreferences p = prefs(context);
        if (!p.getBoolean(ACTIVE, false)) {
            return false;
        }
        long end = p.getLong(END_EPOCH, 0);
        if (end <= System.currentTimeMillis()) {
            stop(context, "expired-locally");
            return false;
        }
        return true;
    }

    static long endEpoch(Context context) {
        return prefs(context).getLong(END_EPOCH, 0);
    }

    static long remainingMillis(Context context) {
        return Math.max(0, endEpoch(context) - System.currentTimeMillis());
    }

    static String policyId(Context context) {
        return prefs(context).getString(POLICY_ID, "none");
    }

    static boolean isTemporarilyAllowed(Context context, String packageName) {
        SharedPreferences p = prefs(context);
        String allowedPackage = p.getString(TEMP_PACKAGE, null);
        long until = p.getLong(TEMP_UNTIL_ELAPSED, 0);
        if (until <= SystemClock.elapsedRealtime()) {
            p.edit().remove(TEMP_PACKAGE).remove(TEMP_UNTIL_ELAPSED).apply();
            return false;
        }
        return packageName != null && packageName.equals(allowedPackage);
    }

    static void grantTemporaryAccess(Context context, String packageName, String reason) {
        long until = SystemClock.elapsedRealtime() + 5 * 60_000L;
        prefs(context).edit()
                .putString(TEMP_PACKAGE, packageName)
                .putLong(TEMP_UNTIL_ELAPSED, until)
                .apply();
        long now = System.currentTimeMillis();
        JSONObject detail = new JSONObject();
        try {
            detail.put("reason", reason);
            detail.put("temporaryUntilElapsed", until);
        } catch (JSONException ignored) {
        }
        ProbeLog.event(context, "temporary_access_started", "overlay", packageName,
                now, now, 0, detail.toString());
    }
}
