package com.jarvis.probe;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;

public final class SupervisionService extends Service {
    private static final String CHANNEL = "jarvis_probe_supervision";
    private static final int NOTIFICATION_ID = 4310;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private ForegroundDetector detector;
    private BlockOverlay overlay;
    private String lastAvailability;
    private String lastNotificationText;

    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            runTick();
            handler.postDelayed(this, 200L);
        }
    };

    @Override
    public void onCreate() {
        super.onCreate();
        createChannel();
        detector = new ForegroundDetector(this);
        overlay = new BlockOverlay(this);
        startForeground(NOTIFICATION_ID, notification("探针正在等待有效策略"));
        handler.post(tick);
        long now = System.currentTimeMillis();
        ProbeLog.event(this, "service_started", "service", null, now, now, 0, null);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        handler.removeCallbacks(tick);
        overlay.hide();
        long now = System.currentTimeMillis();
        ProbeLog.event(this, "service_destroyed", "service", null, now, now, 0,
                PolicyStore.isActive(this) ? "policy-still-active" : "no-active-policy");
        super.onDestroy();
    }

    @Override
    public void onTaskRemoved(Intent rootIntent) {
        if (PolicyStore.isActive(this)) {
            try {
                PolicyScheduler.scheduleServiceRestart(this);
            } catch (RuntimeException exception) {
                long now = System.currentTimeMillis();
                ProbeLog.event(this, "availability", "task-removed", null,
                        now, now, 0, "unavailable:restart-schedule="
                                + exception.getClass().getSimpleName());
            }
        }
        super.onTaskRemoved(rootIntent);
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void runTick() {
        if (!PolicyStore.isActive(this)) {
            overlay.hide();
            stopForeground(STOP_FOREGROUND_REMOVE);
            stopSelf();
            return;
        }

        boolean usage = ProbeCapabilities.hasUsageAccess(this);
        boolean canOverlay = ProbeCapabilities.canOverlay(this);
        boolean notifications = ProbeCapabilities.notificationsEnabled(this);
        boolean exactAlarm = ProbeCapabilities.canScheduleExactAlarms(this);
        String availability = usage && canOverlay && notifications && exactAlarm
                ? "available"
                : "unavailable:usage=" + usage + ",overlay=" + canOverlay
                + ",notifications=" + notifications + ",exactAlarm=" + exactAlarm;
        if (!availability.equals(lastAvailability)) {
            long now = System.currentTimeMillis();
            ProbeLog.event(this, "availability", "service", null, now, now, 0, availability);
            lastAvailability = availability;
        }
        boolean mechanismAvailable = usage && canOverlay;
        if (!mechanismAvailable) {
            overlay.hide();
            updateNotification("监督不可用，请打开缺失权限");
            return;
        }

        detector.poll();
        String current = detector.currentPackage();
        PolicyRules.Decision decision = PolicyRules.decide(
                true,
                true,
                Targets.isBlocked(current),
                PolicyStore.isTemporarilyAllowed(this, current)
        );
        if (decision == PolicyRules.Decision.BLOCK) {
            boolean newBlock = !overlay.isShowing() || !current.equals(overlay.shownPackage());
            overlay.show(current);
            if (newBlock) {
                long now = System.currentTimeMillis();
                ProbeLog.event(this, "blocked", "overlay", current, now, now, 0,
                        "policyId=" + PolicyStore.policyId(this));
            } else {
                overlay.updateRemaining();
            }
            updateNotification("已阻止 " + Targets.label(current));
        } else {
            overlay.hide();
            long minutes = Math.max(1, PolicyStore.remainingMillis(this) / 60_000L);
            updateNotification("监督策略执行中，剩余约 " + minutes + " 分钟");
        }
    }

    private void createChannel() {
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL,
                    "Jarvis 手机阻断探针",
                    NotificationManager.IMPORTANCE_LOW
            );
            channel.setDescription("显示已确认探针策略正在执行或已经降级");
            NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            manager.createNotificationChannel(channel);
        }
    }

    private Notification notification(String text) {
        Intent open = new Intent(this, ProbeActivity.class);
        PendingIntent pending = PendingIntent.getActivity(
                this,
                0,
                open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE
        );
        return new Notification.Builder(this, CHANNEL)
                .setSmallIcon(android.R.drawable.ic_lock_idle_alarm)
                .setContentTitle("Jarvis 手机阻断探针")
                .setContentText(text)
                .setContentIntent(pending)
                .setOngoing(true)
                .build();
    }

    private void updateNotification(String text) {
        if (text.equals(lastNotificationText)) {
            return;
        }
        lastNotificationText = text;
        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        manager.notify(NOTIFICATION_ID, notification(text));
    }
}
