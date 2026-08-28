package com.jarvis.probe;

import android.content.Context;
import android.util.Log;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.io.FileWriter;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

final class ProbeLog {
    static final String TAG = "JarvisProbe";
    private static final String FILE = "probe-events.jsonl";

    private ProbeLog() {
    }

    static synchronized void event(
            Context context,
            String type,
            String source,
            String packageName,
            long eventEpoch,
            long detectedEpoch,
            long latencyMs,
            String detail
    ) {
        JSONObject value = new JSONObject();
        try {
            value.put("id", java.util.UUID.randomUUID().toString());
            value.put("type", type);
            value.put("source", source);
            value.put("package", packageName == null ? JSONObject.NULL : packageName);
            value.put("eventEpochMs", eventEpoch);
            value.put("detectedEpochMs", detectedEpoch);
            value.put("latencyMs", latencyMs);
            value.put("detail", detail == null ? JSONObject.NULL : detail);
        } catch (JSONException ignored) {
        }

        String line = value.toString();
        Log.i(TAG, line);
        File file = new File(context.getFilesDir(), FILE);
        try (FileWriter writer = new FileWriter(file, true)) {
            writer.write(line);
            writer.write('\n');
        } catch (IOException exception) {
            Log.e(TAG, "Could not append probe log", exception);
        }
    }

    static synchronized void clear(Context context) {
        File file = new File(context.getFilesDir(), FILE);
        if (file.exists() && !file.delete()) {
            Log.w(TAG, "Could not delete probe log");
        }
    }

    static synchronized String summary(Context context) {
        File file = new File(context.getFilesDir(), FILE);
        if (!file.exists()) {
            return "还没有测量事件";
        }

        int usageTargetEvents = 0;
        int accessibilityTargetEvents = 0;
        int blocks = 0;
        List<Long> latencies = new ArrayList<>();
        List<String> tail = new ArrayList<>();
        try (BufferedReader reader = new BufferedReader(new FileReader(file))) {
            String line;
            while ((line = reader.readLine()) != null) {
                try {
                    JSONObject value = new JSONObject(line);
                    String type = value.optString("type");
                    String source = value.optString("source");
                    String packageName = value.optString("package", "");
                    if ("foreground".equals(type) && Targets.isBlocked(packageName)) {
                        if ("usage".equals(source)) {
                            usageTargetEvents++;
                            latencies.add(value.optLong("latencyMs", 0));
                        } else if ("accessibility".equals(source)) {
                            accessibilityTargetEvents++;
                        }
                    }
                    if ("blocked".equals(type)) {
                        blocks++;
                    }
                    tail.add(type + " / " + source + " / " + packageName);
                    if (tail.size() > 8) {
                        tail.remove(0);
                    }
                } catch (JSONException ignored) {
                }
            }
        } catch (IOException exception) {
            return "读取测量日志失败：" + exception.getMessage();
        }

        Collections.sort(latencies);
        long p50 = percentile(latencies, 0.50);
        long p95 = percentile(latencies, 0.95);
        long max = latencies.isEmpty() ? 0 : latencies.get(latencies.size() - 1);
        return "使用事件目标命中：" + usageTargetEvents
                + "\n无障碍对照命中：" + accessibilityTargetEvents
                + "\n显示阻断层：" + blocks
                + "\n使用事件延迟 P50/P95/最大：" + p50 + "/" + p95 + "/" + max + " ms"
                + "\n\n最近事件：\n" + String.join("\n", tail);
    }

    private static long percentile(List<Long> sorted, double p) {
        if (sorted.isEmpty()) {
            return 0;
        }
        int index = (int) Math.ceil(p * sorted.size()) - 1;
        return sorted.get(Math.max(0, Math.min(index, sorted.size() - 1)));
    }
}
