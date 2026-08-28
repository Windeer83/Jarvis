package com.jarvis.probe;

import android.content.Context;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.view.Gravity;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

final class BlockOverlay {
    private final Context context;
    private final WindowManager windowManager;
    private LinearLayout root;
    private TextView appName;
    private TextView remaining;
    private EditText reason;
    private String shownPackage;

    BlockOverlay(Context context) {
        this.context = context;
        this.windowManager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
    }

    boolean isShowing() {
        return root != null;
    }

    String shownPackage() {
        return shownPackage;
    }

    void show(String packageName) {
        if (root == null) {
            build();
            WindowManager.LayoutParams params = new WindowManager.LayoutParams(
                    WindowManager.LayoutParams.MATCH_PARENT,
                    WindowManager.LayoutParams.MATCH_PARENT,
                    WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                    WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                            | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
                    android.graphics.PixelFormat.OPAQUE
            );
            params.gravity = Gravity.TOP | Gravity.START;
            windowManager.addView(root, params);
        }
        shownPackage = packageName;
        appName.setText("已阻止打开：" + Targets.label(packageName));
        updateRemaining();
    }

    void updateRemaining() {
        if (remaining == null) {
            return;
        }
        long seconds = PolicyStore.remainingMillis(context) / 1_000L;
        remaining.setText("本次监督剩余 " + (seconds / 60) + " 分 " + (seconds % 60) + " 秒\n"
                + "这不是系统级锁定；它正在验证普通侧载应用的尽力型阻断能力。");
    }

    void hide() {
        if (root != null) {
            try {
                windowManager.removeView(root);
            } catch (IllegalArgumentException ignored) {
            }
        }
        root = null;
        appName = null;
        remaining = null;
        reason = null;
        shownPackage = null;
    }

    private void build() {
        root = new LinearLayout(context);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER_HORIZONTAL);
        root.setPadding(dp(28), dp(72), dp(28), dp(36));
        root.setBackgroundColor(Color.rgb(17, 19, 26));

        TextView eyebrow = text("JARVIS · 已确认监督正在进行", 14, Color.rgb(164, 154, 255));
        root.addView(eyebrow, matchWrap(0));

        appName = text("已阻止打开", 28, Color.WHITE);
        appName.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        root.addView(appName, matchWrap(dp(20)));

        remaining = text("", 16, Color.rgb(205, 208, 220));
        root.addView(remaining, matchWrap(dp(16)));

        reason = new EditText(context);
        reason.setHint("如果确实需要，先写明临时使用原因");
        reason.setTextColor(Color.WHITE);
        reason.setHintTextColor(Color.rgb(145, 149, 166));
        reason.setBackgroundColor(Color.rgb(37, 40, 52));
        reason.setPadding(dp(16), dp(14), dp(16), dp(14));
        root.addView(reason, matchWrap(dp(28)));

        Button temporary = button("填写原因后临时开放 5 分钟");
        temporary.setOnClickListener(view -> {
            String value = reason.getText().toString().trim();
            if (value.isEmpty()) {
                reason.setError("必须填写真实原因");
                reason.requestFocus();
                InputMethodManager keyboard = (InputMethodManager) context.getSystemService(Context.INPUT_METHOD_SERVICE);
                keyboard.showSoftInput(reason, InputMethodManager.SHOW_IMPLICIT);
                return;
            }
            String packageName = shownPackage;
            PolicyStore.grantTemporaryAccess(context, packageName, value);
            Toast.makeText(context, "已临时开放 5 分钟，到期自动恢复阻断", Toast.LENGTH_LONG).show();
            hide();
        });
        root.addView(temporary, matchWrap(dp(14)));

        Button home = button("返回桌面");
        home.setOnClickListener(view -> {
            String packageName = shownPackage;
            long now = System.currentTimeMillis();
            ProbeLog.event(context, "returned_home", "overlay", packageName, now, now, 0, null);
            hide();
            Intent intent = new Intent(Intent.ACTION_MAIN);
            intent.addCategory(Intent.CATEGORY_HOME);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(intent);
        });
        root.addView(home, matchWrap(dp(10)));
    }

    private TextView text(String value, int sp, int color) {
        TextView view = new TextView(context);
        view.setText(value);
        view.setTextSize(sp);
        view.setTextColor(color);
        return view;
    }

    private Button button(String value) {
        Button button = new Button(context);
        button.setText(value);
        button.setTextSize(16);
        button.setAllCaps(false);
        return button;
    }

    private LinearLayout.LayoutParams matchWrap(int topMargin) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
        );
        params.topMargin = topMargin;
        return params;
    }

    private int dp(int value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }
}
