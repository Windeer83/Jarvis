package com.jarvis.mobile;

import android.Manifest;
import android.app.Activity;
import android.app.AlarmManager;
import android.content.Intent;
import android.graphics.Color;
import android.net.Uri;
import android.os.Bundle;
import android.provider.Settings;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;

import java.text.DateFormat;
import java.util.Date;

public final class MainActivity extends Activity {
    private LinearLayout content;
    private TextView status;
    private EditText payload;
    private EditText quickRecord;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        buildUi();
        getWindow().setStatusBarColor(Color.rgb(17, 24, 39));
    }

    @Override protected void onResume() {
        super.onResume();
        renderStatus();
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        IntentResult result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data);
        if (result != null) {
            if (result.getContents() != null) {
                payload.setText(result.getContents());
                pair(result.getContents());
            }
            return;
        }
        super.onActivityResult(requestCode, resultCode, data);
    }

    private void buildUi() {
        ScrollView scroll = new ScrollView(this);
        content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(20), dp(24), dp(20), dp(36));
        content.setBackgroundColor(Color.rgb(244, 245, 247));
        TextView title = text("Jarvis Mobile", 28, Color.rgb(17, 24, 39));
        title.setTypeface(null, android.graphics.Typeface.BOLD);
        status = text("正在读取状态…", 16, Color.rgb(55, 65, 81));
        content.addView(title);
        content.addView(status, marginTop(8));

        content.addView(section("配对"), marginTop(24));
        payload = input("扫描电脑端二维码，或粘贴配对内容");
        content.addView(payload, marginTop(8));
        LinearLayout pairActions = row();
        Button scan = button("扫描二维码");
        scan.setOnClickListener(ignored -> new IntentIntegrator(this)
                .setDesiredBarcodeFormats(IntentIntegrator.QR_CODE)
                .setPrompt("扫描 Jarvis Desktop 配对码").initiateScan());
        Button pair = button("开始配对");
        pair.setOnClickListener(ignored -> pair(payload.getText().toString().trim()));
        pairActions.addView(scan, weight());
        pairActions.addView(pair, weightWithLeft(8));
        content.addView(pairActions, marginTop(8));

        content.addView(section("必要权限"), marginTop(24));
        content.addView(permissionButton("打开使用情况访问权限", () ->
                startActivity(new Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS))));
        content.addView(permissionButton("打开悬浮窗权限", () ->
                startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                        Uri.parse("package:" + getPackageName())))), marginTop(8));
        content.addView(permissionButton("打开通知权限", () -> {
            if (android.os.Build.VERSION.SDK_INT >= 33)
                requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 90);
        }), marginTop(8));
        content.addView(permissionButton("打开精确闹钟权限", () -> {
            if (android.os.Build.VERSION.SDK_INT >= 31)
                startActivity(new Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM,
                        Uri.parse("package:" + getPackageName())));
        }), marginTop(8));
        content.addView(permissionButton("打开应用详情（设置后台运行）", () ->
                startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                        Uri.parse("package:" + getPackageName())))), marginTop(8));
        Button confirmed = button("我已完成华为后台运行设置");
        confirmed.setOnClickListener(ignored -> {
            ConnectionStore.setBackgroundConfirmed(this, true);
            renderStatus();
        });
        content.addView(confirmed, marginTop(8));

        content.addView(section("快速记录"), marginTop(24));
        quickRecord = input("写下当前想法或说明");
        content.addView(quickRecord, marginTop(8));
        Button record = button("记录并等待同步");
        record.setOnClickListener(ignored -> {
            String value = quickRecord.getText().toString().trim();
            if (value.isEmpty()) return;
            EventOutbox.enqueue(this, "QuickRecord", PolicyStore.read(this), null, value, null);
            quickRecord.setText("");
            Toast.makeText(this, "已保存在本机，联网后同步", Toast.LENGTH_SHORT).show();
        });
        content.addView(record, marginTop(8));
        scroll.addView(content);
        setContentView(scroll);
    }

    private void pair(String value) {
        if (value.isEmpty()) {
            Toast.makeText(this, "请先扫描或粘贴配对内容", Toast.LENGTH_SHORT).show();
            return;
        }
        status.setText("正在验证电脑证书并配对…");
        new Thread(() -> {
            try {
                SyncClient.pair(this, value);
                startForegroundService(new Intent(this, MobileRuntimeService.class));
                runOnUiThread(() -> {
                    payload.setText("");
                    Toast.makeText(this, "配对成功", Toast.LENGTH_SHORT).show();
                    renderStatus();
                });
            } catch (Exception exception) {
                runOnUiThread(() -> {
                    status.setText("配对失败：" + exception.getMessage());
                    Toast.makeText(this, "配对失败", Toast.LENGTH_SHORT).show();
                });
            }
        }, "jarvis-pair").start();
    }

    private void renderStatus() {
        boolean paired = ConnectionStore.isPaired(this);
        MobilePolicy policy = PolicyStore.read(this);
        StringBuilder value = new StringBuilder();
        value.append(paired ? "已配对" : "尚未配对");
        value.append("\n使用情况：").append(Capabilities.usageAccess(this) ? "可用" : "未授权");
        value.append(" · 覆盖层：").append(Capabilities.overlay(this) ? "可用" : "未授权");
        value.append("\n通知：").append(Capabilities.notifications(this) ? "可用" : "未授权");
        value.append(" · 到期闹钟：").append(Capabilities.exactAlarm(this) ? "精确" : "降级");
        value.append(" · 后台：").append(ConnectionStore.backgroundConfirmed(this) ? "已确认" : "待确认");
        if (policy != null) {
            value.append("\n当前缓存：").append(policy.title);
            value.append("（").append(policy.isActive(System.currentTimeMillis()) ? "执行中" : "待开始").append("）");
        }
        long lastSync = RuntimeState.lastSync(this);
        if (lastSync > 0) value.append("\n最近同步：")
                .append(DateFormat.getDateTimeInstance().format(new Date(lastSync)));
        String error = RuntimeState.lastError(this);
        if (error != null) value.append("\n当前降级：").append(error);
        status.setText(value.toString());
    }

    private TextView section(String value) {
        TextView text = text(value, 20, Color.rgb(17, 24, 39));
        text.setTypeface(null, android.graphics.Typeface.BOLD);
        return text;
    }

    private TextView text(String value, int size, int color) {
        TextView text = new TextView(this);
        text.setText(value); text.setTextSize(size); text.setTextColor(color);
        return text;
    }

    private EditText input(String hint) {
        EditText value = new EditText(this);
        value.setHint(hint); value.setBackgroundColor(Color.WHITE);
        value.setPadding(dp(12), dp(10), dp(12), dp(10)); value.setMinHeight(dp(52));
        return value;
    }

    private Button button(String value) {
        Button button = new Button(this); button.setText(value); button.setMinHeight(dp(48));
        return button;
    }

    private View permissionButton(String label, Runnable action) {
        Button value = button(label); value.setOnClickListener(ignored -> action.run()); return value;
    }

    private LinearLayout row() {
        LinearLayout value = new LinearLayout(this); value.setOrientation(LinearLayout.HORIZONTAL); return value;
    }

    private LinearLayout.LayoutParams weight() {
        return new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
    }

    private LinearLayout.LayoutParams weightWithLeft(int left) {
        LinearLayout.LayoutParams value = weight(); value.leftMargin = dp(left); return value;
    }

    private LinearLayout.LayoutParams marginTop(int top) {
        LinearLayout.LayoutParams value = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        value.topMargin = dp(top); return value;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
