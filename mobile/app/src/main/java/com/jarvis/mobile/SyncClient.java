package com.jarvis.mobile;

import android.content.Context;
import android.os.Build;

import org.json.JSONArray;
import org.json.JSONObject;

import java.time.Instant;

final class SyncClient {
    private SyncClient() { }

    static void pair(Context context, String qrPayload) throws Exception {
        android.net.Uri uri = android.net.Uri.parse(qrPayload);
        if (!"jarvis".equals(uri.getScheme()) || !"pair".equals(uri.getHost()))
            throw new IllegalArgumentException("这不是 Jarvis 配对码");
        String endpoint = require(uri.getQueryParameter("endpoint"));
        String fingerprint = require(uri.getQueryParameter("fingerprint"));
        String secret = require(uri.getQueryParameter("secret"));
        JSONObject request = new JSONObject();
        request.put("protocolVersion", 1);
        request.put("deviceId", ConnectionStore.deviceId(context));
        request.put("deviceName", Build.MANUFACTURER + " " + Build.MODEL);
        request.put("oneTimeSecret", secret);
        JSONObject response = PinnedHttpClient.post(
                endpoint, "/v1/pair", fingerprint, null, request);
        ConnectionStore.savePairing(context, endpoint, fingerprint,
                response.getString("deviceToken"));
    }

    static void synchronize(Context context) throws Exception {
        String endpoint = ConnectionStore.endpoint(context);
        String fingerprint = ConnectionStore.fingerprint(context);
        String token = ConnectionStore.token(context);
        if (endpoint == null || fingerprint == null || token == null) return;
        MobilePolicy policy = PolicyStore.read(context);
        JSONObject health = new JSONObject();
        health.put("deviceId", ConnectionStore.deviceId(context));
        health.put("observedAt", Instant.now().toString());
        health.put("state", Capabilities.usageAccess(context) && Capabilities.overlay(context)
                ? "Ready" : "Degraded");
        health.put("usageAccess", Capabilities.usageAccess(context));
        health.put("overlay", Capabilities.overlay(context));
        health.put("notifications", Capabilities.notifications(context));
        health.put("exactAlarm", Capabilities.exactAlarm(context));
        health.put("backgroundAllowed", ConnectionStore.backgroundConfirmed(context));
        if (policy != null && policy.isActive(System.currentTimeMillis())) {
            health.put("activeCommitmentId", policy.commitmentId);
            health.put("activePolicyVersion", policy.version);
        }
        JSONObject request = new JSONObject();
        request.put("protocolVersion", 1);
        request.put("deviceId", ConnectionStore.deviceId(context));
        request.put("health", health);
        request.put("pendingEvents", EventOutbox.pending(context));
        JSONObject response;
        try {
            response = PinnedHttpClient.post(endpoint, "/v1/sync", fingerprint, token, request);
        } catch (PinnedHttpClient.HttpFailure failure) {
            if (failure.status == 401) ConnectionStore.clear(context);
            throw failure;
        }
        PolicyStore.applyDirective(context, response.getJSONObject("directive"));
        EventOutbox.acknowledge(context, response.getJSONArray("acceptedEventIds"));
    }

    private static String require(String value) {
        if (value == null || value.isEmpty()) throw new IllegalArgumentException("配对码缺少必要字段");
        return value;
    }
}
