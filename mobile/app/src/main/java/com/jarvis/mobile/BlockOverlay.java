package com.jarvis.mobile;

import android.content.Context;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.graphics.Typeface;
import android.provider.Settings;
import android.view.Gravity;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

final class BlockOverlay {
    private final Context context;
    private final WindowManager windowManager;
    private LinearLayout view;
    private String packageName;

    BlockOverlay(Context context) {
        this.context = context.getApplicationContext();
        windowManager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
    }

    boolean isShowingFor(String value) { return view != null && value.equals(packageName); }

    void show(MobilePolicy policy, String blockedPackage) {
        if (!Settings.canDrawOverlays(context) || isShowingFor(blockedPackage)) return;
        hide();
        packageName = blockedPackage;
        view = new LinearLayout(context);
        view.setOrientation(LinearLayout.VERTICAL);
        view.setGravity(Gravity.CENTER);
        view.setPadding(dp(28), dp(44), dp(28), dp(44));
        view.setBackgroundColor(Color.rgb(17, 24, 39));

        TextView title = text("现在是已确认的工作时间", 25, true);
        TextView target = text(policy.title, 18, false);
        target.setTextColor(Color.rgb(191, 201, 220));
        target.setPadding(0, dp(12), 0, dp(28));
        EditText reason = new EditText(context);
        reason.setHint("如确有需要，请填写临时开放原因");
        reason.setTextColor(Color.WHITE);
        reason.setHintTextColor(Color.rgb(156, 163, 175));
        reason.setSingleLine(false);
        reason.setMinHeight(dp(56));
        Button returnButton = button("返回工作");
        returnButton.setOnClickListener(ignored -> {
            context.startActivity(new android.content.Intent(android.content.Intent.ACTION_MAIN)
                    .addCategory(android.content.Intent.CATEGORY_HOME)
                    .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK));
            hide();
        });
        Button allowButton = button("临时开放 5 分钟");
        allowButton.setOnClickListener(ignored -> {
            String value = reason.getText().toString().trim();
            if (value.isEmpty()) {
                Toast.makeText(context, "请先填写原始原因", Toast.LENGTH_SHORT).show();
                return;
            }
            PolicyStore.grantTemporaryAccess(context, blockedPackage, value);
            hide();
        });
        view.addView(title, matchWrap());
        view.addView(target, matchWrap());
        view.addView(reason, matchWrap());
        view.addView(returnButton, matchWrapWithTop(24));
        view.addView(allowButton, matchWrapWithTop(12));

        WindowManager.LayoutParams params = new WindowManager.LayoutParams(
                WindowManager.LayoutParams.MATCH_PARENT,
                WindowManager.LayoutParams.MATCH_PARENT,
                WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN |
                        WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
                PixelFormat.OPAQUE);
        windowManager.addView(view, params);
        EventOutbox.enqueue(context, "AppBlocked", policy, blockedPackage, null, null);
    }

    void hide() {
        if (view == null) return;
        try { windowManager.removeView(view); } catch (IllegalArgumentException ignored) { }
        view = null;
        packageName = null;
    }

    private TextView text(String value, int size, boolean bold) {
        TextView text = new TextView(context);
        text.setText(value);
        text.setTextSize(size);
        text.setTextColor(Color.WHITE);
        text.setGravity(Gravity.CENTER);
        if (bold) text.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return text;
    }

    private Button button(String value) {
        Button button = new Button(context);
        button.setText(value);
        button.setMinHeight(dp(52));
        return button;
    }

    private LinearLayout.LayoutParams matchWrap() {
        return new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
    }

    private LinearLayout.LayoutParams matchWrapWithTop(int top) {
        LinearLayout.LayoutParams value = matchWrap();
        value.topMargin = dp(top);
        return value;
    }

    private int dp(int value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }
}
