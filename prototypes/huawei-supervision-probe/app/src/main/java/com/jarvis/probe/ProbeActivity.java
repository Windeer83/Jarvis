package com.jarvis.probe;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.text.DateFormat;
import java.util.Date;
import java.util.Map;

public final class ProbeActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private TextView state;
    private TextView measurements;
    private boolean visible;

    private final Runnable refresh = new Runnable() {
        @Override
        public void run() {
            renderState();
            if (visible) {
                handler.postDelayed(this, 1_000L);
            }
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(buildView());
        handleAutomationIntent(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleAutomationIntent(intent);
    }

    @Override
    protected void onResume() {
        super.onResume();
        visible = true;
        handler.removeCallbacks(refresh);
        handler.post(refresh);
    }

    @Override
    protected void onPause() {
        visible = false;
        handler.removeCallbacks(refresh);
        super.onPause();
    }

    private View buildView() {
        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(Color.rgb(17, 19, 26));
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(20), dp(36), dp(20), dp(40));
        scroll.addView(content);

        TextView eyebrow = text("THROWAWAY DEVICE PROBE", 12, Color.rgb(164, 154, 255));
        eyebrow.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        content.addView(eyebrow);

        TextView title = text("Jarvis 手机阻断探针", 27, Color.WHITE);
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        content.addView(title, params(dp(7)));

        content.addView(text(
                "仅验证本机前台包识别、全屏覆盖、五分钟临时开放和离线到期。不会读取目标应用内容。",
                15,
                Color.rgb(194, 198, 213)
        ), params(dp(10)));

        state = text("正在读取状态…", 14, Color.WHITE);
        state.setBackgroundColor(Color.rgb(34, 37, 49));
        state.setPadding(dp(14), dp(14), dp(14), dp(14));
        content.addView(state, params(dp(22)));

        content.addView(section("逐项授权（都可以随时撤销）"), params(dp(24)));
        content.addView(button("1. 打开使用情况访问", view -> open(Settings.ACTION_USAGE_ACCESS_SETTINGS)));
        content.addView(button("2. 允许显示在其他应用上层", view -> openOverlaySettings()), params(dp(8)));
        content.addView(button("3. 允许通知", view -> requestNotifications()), params(dp(8)));
        content.addView(button("4. 查看精确定时特殊访问", view -> openExactAlarmSettings()), params(dp(8)));
        content.addView(button("5. 查看电池优化/后台运行", view -> open(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)), params(dp(8)));
        content.addView(button("6. 可选：开启无障碍测量对照", view -> open(Settings.ACTION_ACCESSIBILITY_SETTINGS)), params(dp(8)));
        content.addView(text(
                "无障碍仅记录窗口包名和时间，不读取控件；它不参与阻断。首选路径通过后不会进入正式实现。华为“应用启动管理”仍需在系统设置中手动允许后台活动。",
                13,
                Color.rgb(161, 165, 181)
        ), params(dp(8)));

        content.addView(section("本地策略测试"), params(dp(24)));
        content.addView(button("启动 10 分钟探针策略", view -> startPolicy(10)));
        content.addView(button("启动 30 分钟离线策略", view -> startPolicy(30)), params(dp(8)));
        content.addView(button("停止探针策略", view -> stopPolicy()), params(dp(8)));

        content.addView(section("测量结果"), params(dp(24)));
        measurements = text("还没有测量事件", 13, Color.rgb(218, 220, 231));
        measurements.setBackgroundColor(Color.rgb(34, 37, 49));
        measurements.setPadding(dp(14), dp(14), dp(14), dp(14));
        content.addView(measurements);
        content.addView(button("清空测量日志", view -> {
            ProbeLog.clear(this);
            renderState();
        }), params(dp(8)));

        return scroll;
    }

    private void startPolicy(int minutes) {
        PolicyStore.start(this, minutes * 60_000L, "activity");
        Intent service = new Intent(this, SupervisionService.class);
        if (Build.VERSION.SDK_INT >= 26) {
            startForegroundService(service);
        } else {
            startService(service);
        }
        if (!ProbeCapabilities.readyToBlock(this)) {
            Toast.makeText(this, "策略已创建，但权限不完整：当前明确标记为监督不可用", Toast.LENGTH_LONG).show();
        } else {
            Toast.makeText(this, "探针策略已启动", Toast.LENGTH_SHORT).show();
        }
        renderState();
    }

    private void handleAutomationIntent(Intent intent) {
        if (intent == null) {
            return;
        }
        if (intent.getBooleanExtra("clearLog", false)) {
            ProbeLog.clear(this);
            intent.removeExtra("clearLog");
        }
        int minutes = intent.getIntExtra("startMinutes", 0);
        if (minutes > 0 && minutes <= 120) {
            intent.removeExtra("startMinutes");
            startPolicy(minutes);
        }
    }

    private void stopPolicy() {
        PolicyStore.stop(this, "owner-stopped-probe");
        stopService(new Intent(this, SupervisionService.class));
        renderState();
    }

    private void renderState() {
        StringBuilder value = new StringBuilder();
        value.append("设备：")
                .append(Build.MANUFACTURER).append(' ')
                .append(Build.MODEL).append('\n')
                .append("Android API：").append(Build.VERSION.SDK_INT)
                .append(" / release ").append(Build.VERSION.RELEASE).append('\n')
                .append("Build：").append(Build.DISPLAY).append('\n')
                .append("探针版本：").append(BuildConfig.VERSION_NAME)
                .append(" (").append(BuildConfig.VERSION_CODE).append(")\n\n")
                .append(mark(ProbeCapabilities.hasUsageAccess(this))).append(" 使用情况访问\n")
                .append(mark(ProbeCapabilities.canOverlay(this))).append(" 显示在其他应用上层\n")
                .append(mark(ProbeCapabilities.hasNotificationPermission(this)
                        && ProbeCapabilities.notificationsEnabled(this))).append(" 通知可见\n")
                .append(mark(ProbeCapabilities.canScheduleExactAlarms(this))).append(" 精确定时特殊访问\n")
                .append(mark(ProbeCapabilities.batteryUnrestricted(this))).append(" 已忽略电池优化\n")
                .append(mark(ProbeCapabilities.accessibilityComparisonEnabled(this))).append(" 无障碍测量对照（可选）\n\n")
                .append("目标包：\n");
        for (Map.Entry<String, String> target : Targets.PACKAGES.entrySet()) {
            value.append(packageLine(target.getValue(), target.getKey())).append('\n');
        }
        value.append('\n');
        if (PolicyStore.isActive(this)) {
            value.append("策略：执行中\n")
                    .append("ID：").append(PolicyStore.policyId(this)).append('\n')
                    .append("结束：").append(DateFormat.getDateTimeInstance().format(
                            new Date(PolicyStore.endEpoch(this)))).append('\n')
                    .append("当前执行能力：")
                    .append(ProbeCapabilities.readyToBlock(this) ? "可用" : "不可用/降级");
        } else {
            value.append("策略：未运行");
        }
        state.setText(value.toString());
        measurements.setText(ProbeLog.summary(this));
    }

    private String packageLine(String label, String packageName) {
        try {
            PackageInfo info = getPackageManager().getPackageInfo(packageName, 0);
            return "✓ " + label + "：" + packageName + " / " + info.versionName;
        } catch (PackageManager.NameNotFoundException ignored) {
            return "? " + label + "：未找到候选包 " + packageName;
        }
    }

    private void requestNotifications() {
        if (Build.VERSION.SDK_INT >= 33) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 7301);
        } else {
            Intent intent = new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS);
            intent.putExtra(Settings.EXTRA_APP_PACKAGE, getPackageName());
            startActivity(intent);
        }
    }

    private void openOverlaySettings() {
        Intent intent = new Intent(
                Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                Uri.parse("package:" + getPackageName())
        );
        startActivity(intent);
    }

    private void openExactAlarmSettings() {
        if (Build.VERSION.SDK_INT >= 31) {
            Intent intent = new Intent(
                    Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM,
                    Uri.parse("package:" + getPackageName())
            );
            startActivity(intent);
        } else {
            Toast.makeText(this, "当前 Android API 不需要单独授权", Toast.LENGTH_SHORT).show();
        }
    }

    private void open(String action) {
        try {
            startActivity(new Intent(action));
        } catch (RuntimeException exception) {
            Intent fallback = new Intent(
                    Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                    Uri.parse("package:" + getPackageName())
            );
            startActivity(fallback);
        }
    }

    private TextView section(String value) {
        TextView view = text(value, 18, Color.WHITE);
        view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return view;
    }

    private Button button(String value, View.OnClickListener listener) {
        Button button = new Button(this);
        button.setText(value);
        button.setTextSize(15);
        button.setAllCaps(false);
        button.setGravity(Gravity.CENTER_VERTICAL | Gravity.START);
        button.setOnClickListener(listener);
        return button;
    }

    private TextView text(String value, int sp, int color) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(sp);
        view.setTextColor(color);
        return view;
    }

    private LinearLayout.LayoutParams params(int topMargin) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
        );
        params.topMargin = topMargin;
        return params;
    }

    private String mark(boolean value) {
        return value ? "✓" : "✕";
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
