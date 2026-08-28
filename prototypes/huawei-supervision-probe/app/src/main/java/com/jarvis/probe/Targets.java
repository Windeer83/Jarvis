package com.jarvis.probe;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

final class Targets {
    static final Map<String, String> PACKAGES;

    static {
        LinkedHashMap<String, String> packages = new LinkedHashMap<>();
        packages.put("com.ss.android.ugc.aweme", "抖音");
        packages.put("tv.danmaku.bili", "哔哩哔哩");
        packages.put("com.xingin.xhs", "小红书");
        packages.put("com.tencent.mm", "微信");
        PACKAGES = Collections.unmodifiableMap(packages);
    }

    private Targets() {
    }

    static boolean isBlocked(String packageName) {
        return packageName != null && PACKAGES.containsKey(packageName);
    }

    static String label(String packageName) {
        String label = PACKAGES.get(packageName);
        return label == null ? packageName : label;
    }
}
