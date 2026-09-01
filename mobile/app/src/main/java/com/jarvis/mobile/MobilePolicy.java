package com.jarvis.mobile;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.time.OffsetDateTime;
import java.util.ArrayList;
import java.util.List;

final class MobilePolicy {
    final long generation;
    final String commitmentId;
    final int version;
    final long startEpoch;
    final long endEpoch;
    final String title;
    final List<String> blockedPackages;

    MobilePolicy(long generation, String commitmentId, int version, long startEpoch,
                 long endEpoch, String title, List<String> blockedPackages) {
        this.generation = generation;
        this.commitmentId = commitmentId;
        this.version = version;
        this.startEpoch = startEpoch;
        this.endEpoch = endEpoch;
        this.title = title;
        this.blockedPackages = List.copyOf(blockedPackages);
    }

    boolean isActive(long now) {
        return now >= startEpoch && now < endEpoch;
    }

    boolean blocks(String packageName) {
        return packageName != null && blockedPackages.contains(packageName);
    }

    JSONObject toJson() throws JSONException {
        JSONObject value = new JSONObject();
        value.put("generation", generation);
        value.put("commitmentId", commitmentId);
        value.put("version", version);
        value.put("startEpoch", startEpoch);
        value.put("endEpoch", endEpoch);
        value.put("title", title);
        value.put("blockedPackages", new JSONArray(blockedPackages));
        return value;
    }

    static MobilePolicy fromStoredJson(JSONObject value) throws JSONException {
        JSONArray packages = value.getJSONArray("blockedPackages");
        List<String> values = new ArrayList<>();
        for (int i = 0; i < packages.length(); i++) values.add(packages.getString(i));
        return new MobilePolicy(
                value.getLong("generation"), value.getString("commitmentId"),
                value.getInt("version"), value.getLong("startEpoch"),
                value.getLong("endEpoch"), value.getString("title"), values);
    }

    static MobilePolicy fromDirective(JSONObject directive) throws JSONException {
        JSONObject policy = directive.getJSONObject("policy");
        JSONArray packages = policy.getJSONArray("blockedPackages");
        List<String> values = new ArrayList<>();
        for (int i = 0; i < packages.length(); i++) values.add(packages.getString(i));
        return new MobilePolicy(
                directive.getLong("generation"),
                policy.getString("commitmentId"), policy.getInt("version"),
                OffsetDateTime.parse(policy.getString("startAt")).toInstant().toEpochMilli(),
                OffsetDateTime.parse(policy.getString("endAt")).toInstant().toEpochMilli(),
                policy.getString("displayTitle"), values);
    }
}
