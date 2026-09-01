package com.jarvis.mobile;

import android.content.Context;

final class RuntimeState {
    private static final String FILE = "jarvis_mobile_runtime";
    private RuntimeState() { }

    static void success(Context context) {
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).edit()
                .putLong("last_sync", System.currentTimeMillis()).remove("last_error").apply();
    }

    static void failure(Context context, Exception error) {
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).edit()
                .putString("last_error", error.getClass().getSimpleName() + ": " + error.getMessage()).apply();
    }

    static long lastSync(Context context) {
        return context.getSharedPreferences(FILE, Context.MODE_PRIVATE).getLong("last_sync", 0);
    }

    static String lastError(Context context) {
        return context.getSharedPreferences(FILE, Context.MODE_PRIVATE).getString("last_error", null);
    }
}
