package com.jarvis.mobile;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.time.Instant;
import java.util.HashSet;
import java.util.Set;
import java.util.UUID;

final class EventOutbox {
    private static final String FILE = "jarvis_mobile_outbox";
    private static final String EVENTS = "events";
    private static final int LIMIT = 200;

    private EventOutbox() { }

    static synchronized void enqueue(Context context, String kind, MobilePolicy policy,
                                     String packageName, String reason, String detail) {
        try {
            JSONArray values = read(context);
            JSONObject value = new JSONObject();
            value.put("eventId", UUID.randomUUID().toString());
            value.put("kind", kind);
            value.put("occurredAt", Instant.now().toString());
            if (policy != null) {
                value.put("commitmentId", policy.commitmentId);
                value.put("policyVersion", policy.version);
            }
            if (packageName != null) value.put("packageName", packageName);
            if (reason != null) value.put("reason", reason);
            if (detail != null) value.put("detail", detail);
            values.put(value);
            while (values.length() > LIMIT) values.remove(0);
            save(context, values);
        } catch (JSONException ignored) { }
    }

    static synchronized JSONArray pending(Context context) {
        try { return new JSONArray(read(context).toString()); }
        catch (JSONException ignored) { return new JSONArray(); }
    }

    static synchronized void acknowledge(Context context, JSONArray ids) {
        Set<String> accepted = new HashSet<>();
        for (int i = 0; i < ids.length(); i++) accepted.add(ids.optString(i));
        JSONArray kept = new JSONArray();
        JSONArray current = read(context);
        for (int i = 0; i < current.length(); i++) {
            JSONObject value = current.optJSONObject(i);
            if (value != null && !accepted.contains(value.optString("eventId"))) kept.put(value);
        }
        save(context, kept);
    }

    private static JSONArray read(Context context) {
        SharedPreferences preferences = context.getSharedPreferences(FILE, Context.MODE_PRIVATE);
        try { return new JSONArray(preferences.getString(EVENTS, "[]")); }
        catch (JSONException ignored) { return new JSONArray(); }
    }

    private static void save(Context context, JSONArray value) {
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE)
                .edit().putString(EVENTS, value.toString()).apply();
    }
}
