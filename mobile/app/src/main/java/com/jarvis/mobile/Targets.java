package com.jarvis.mobile;

import java.util.List;

final class Targets {
    static final String DOUYIN = "com.ss.android.ugc.aweme";
    static final String BILIBILI = "tv.danmaku.bili";
    static final String XIAOHONGSHU = "com.xingin.xhs";
    static final String WECHAT = "com.tencent.mm";
    static final List<String> DEFAULTS = List.of(DOUYIN, BILIBILI, XIAOHONGSHU, WECHAT);

    private Targets() { }
}
