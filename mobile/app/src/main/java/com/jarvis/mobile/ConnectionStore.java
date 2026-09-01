package com.jarvis.mobile;

import android.content.Context;
import android.content.SharedPreferences;

import java.util.Locale;
import java.util.UUID;

final class ConnectionStore {
    private static final String FILE = "jarvis_mobile_connection";
    private static final String ENDPOINT = "endpoint";
    private static final String FINGERPRINT = "fingerprint";
    private static final String BACKGROUND_CONFIRMED = "background_confirmed";
    private static final String DEVICE_ID = "device_id";

    private ConnectionStore() { }

    static void savePairing(Context context, String endpoint, String fingerprint, String token)
            throws Exception {
        prefs(context).edit().putString(ENDPOINT, endpoint)
                .putString(FINGERPRINT, normalize(fingerprint)).apply();
        SecretStore.save(context, token);
    }

    static boolean isPaired(Context context) {
        return endpoint(context) != null && fingerprint(context) != null && SecretStore.read(context) != null;
    }

    static String endpoint(Context context) { return prefs(context).getString(ENDPOINT, null); }
    static String fingerprint(Context context) { return prefs(context).getString(FINGERPRINT, null); }
    static String token(Context context) { return SecretStore.read(context); }

    static String deviceId(Context context) {
        String value = prefs(context).getString(DEVICE_ID, null);
        if (value != null) return value;
        value = UUID.randomUUID().toString();
        prefs(context).edit().putString(DEVICE_ID, value).apply();
        return value;
    }

    static void setBackgroundConfirmed(Context context, boolean value) {
        prefs(context).edit().putBoolean(BACKGROUND_CONFIRMED, value).apply();
    }

    static boolean backgroundConfirmed(Context context) {
        return prefs(context).getBoolean(BACKGROUND_CONFIRMED, false);
    }

    static void clear(Context context) {
        prefs(context).edit().clear().apply();
        SecretStore.clear(context);
    }

    static String normalize(String value) {
        return value == null ? null : value.replace(":", "").trim().toUpperCase(Locale.ROOT);
    }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }
}
