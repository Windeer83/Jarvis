package com.jarvis.mobile;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.SystemClock;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

final class PolicyStore {
    private static final String FILE = "jarvis_mobile_policy";
    private static final String POLICY_JSON = "policy_json";
    private static final String TEMP_PACKAGE = "temp_package";
    private static final String TEMP_UNTIL_ELAPSED = "temp_until_elapsed";

    private PolicyStore() { }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    static MobilePolicy read(Context context) {
        String json = prefs(context).getString(POLICY_JSON, null);
        if (json == null) return null;
        try {
            return MobilePolicy.fromStoredJson(new JSONObject(json));
        } catch (JSONException exception) {
            prefs(context).edit().remove(POLICY_JSON).apply();
            return null;
        }
    }

    static boolean applyDirective(Context context, JSONObject directive) throws JSONException {
        JSONArray revoked = directive.optJSONArray("revokedCommitmentIds");
        MobilePolicy current = read(context);
        if (current != null && revoked != null) {
            for (int i = 0; i < revoked.length(); i++) {
                if (current.commitmentId.equals(revoked.getString(i))) {
                    clear(context, "revoked");
                    current = null;
                    break;
                }
            }
        }
        if (directive.isNull("policy")) {
            if (current != null) clear(context, "server-cleared");
            return true;
        }

        MobilePolicy incoming = MobilePolicy.fromDirective(directive);
        if (!PolicyRules.shouldReplace(
                current == null ? null : current.commitmentId,
                current == null ? 0 : current.version,
                incoming.commitmentId, incoming.version)) return false;
        prefs(context).edit()
                .putString(POLICY_JSON, incoming.toJson().toString())
                .remove(TEMP_PACKAGE).remove(TEMP_UNTIL_ELAPSED).apply();
        PolicyScheduler.scheduleExpiry(context, incoming.endEpoch);
        EventOutbox.enqueue(context, "PolicyAccepted", incoming, null, null, null);
        return true;
    }

    static void clear(Context context, String reason) {
        MobilePolicy current = read(context);
        prefs(context).edit().remove(POLICY_JSON)
                .remove(TEMP_PACKAGE).remove(TEMP_UNTIL_ELAPSED).apply();
        PolicyScheduler.cancelExpiry(context);
        if (current != null)
            EventOutbox.enqueue(context, "PolicyExpired", current, null, reason, null);
    }

    static boolean isTemporarilyAllowed(Context context, String packageName) {
        SharedPreferences value = prefs(context);
        long until = value.getLong(TEMP_UNTIL_ELAPSED, 0);
        if (until <= SystemClock.elapsedRealtime()) {
            String expiredPackage = value.getString(TEMP_PACKAGE, null);
            value.edit().remove(TEMP_PACKAGE).remove(TEMP_UNTIL_ELAPSED).apply();
            if (expiredPackage != null)
                EventOutbox.enqueue(context, "TemporaryAccessEnded", read(context),
                        expiredPackage, null, "expired-locally");
            return false;
        }
        return packageName != null && packageName.equals(value.getString(TEMP_PACKAGE, null));
    }

    static void grantTemporaryAccess(Context context, String packageName, String reason) {
        MobilePolicy policy = read(context);
        if (policy == null || reason == null || reason.trim().isEmpty()) return;
        prefs(context).edit().putString(TEMP_PACKAGE, packageName)
                .putLong(TEMP_UNTIL_ELAPSED, SystemClock.elapsedRealtime() + 5 * 60_000L).apply();
        EventOutbox.enqueue(context, "TemporaryAccessStarted", policy, packageName, reason.trim(), null);
    }

    static void clearTemporaryAccessAfterBoot(Context context) {
        prefs(context).edit().remove(TEMP_PACKAGE).remove(TEMP_UNTIL_ELAPSED).apply();
    }
}
