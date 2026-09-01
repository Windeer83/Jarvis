package com.jarvis.mobile;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

public final class MobileRuntimeService extends Service {
    private static final String CHANNEL = "jarvis-supervision-runtime";
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService network = Executors.newSingleThreadExecutor();
    private final AtomicBoolean syncing = new AtomicBoolean();
    private ForegroundDetector detector;
    private BlockOverlay overlay;
    private long nextSyncAt;

    @Override public void onCreate() {
        super.onCreate();
        detector = new ForegroundDetector(this);
        overlay = new BlockOverlay(this);
        createChannel();
        startForeground(71, notification("手机监督运行中"));
        handler.post(loop);
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        return START_STICKY;
    }

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override public void onDestroy() {
        handler.removeCallbacks(loop);
        overlay.hide();
        network.shutdownNow();
        super.onDestroy();
    }

    private final Runnable loop = new Runnable() {
        @Override public void run() {
            MobilePolicy policy = PolicyStore.read(MobileRuntimeService.this);
            long now = System.currentTimeMillis();
            if (policy != null && now >= policy.endEpoch) {
                PolicyStore.clear(MobileRuntimeService.this, "expired-locally");
                policy = null;
            }
            String foreground = Capabilities.usageAccess(MobileRuntimeService.this)
                    ? detector.poll() : null;
            PolicyRules.Decision decision = PolicyRules.decide(
                    policy != null && policy.isActive(now),
                    Capabilities.usageAccess(MobileRuntimeService.this) &&
                            Capabilities.overlay(MobileRuntimeService.this),
                    policy != null && policy.blocks(foreground),
                    PolicyStore.isTemporarilyAllowed(MobileRuntimeService.this, foreground));
            if (decision == PolicyRules.Decision.BLOCK) overlay.show(policy, foreground);
            else overlay.hide();

            if (ConnectionStore.isPaired(MobileRuntimeService.this) && now >= nextSyncAt &&
                    syncing.compareAndSet(false, true)) {
                nextSyncAt = now + 5_000L;
                network.execute(() -> {
                    try {
                        SyncClient.synchronize(MobileRuntimeService.this);
                        RuntimeState.success(MobileRuntimeService.this);
                    } catch (Exception exception) {
                        RuntimeState.failure(MobileRuntimeService.this, exception);
                    } finally { syncing.set(false); }
                });
            }
            handler.postDelayed(this, 250L);
        }
    };

    private void createChannel() {
        NotificationChannel channel = new NotificationChannel(
                CHANNEL, "Jarvis 手机监督", NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("配对同步与已确认承诺的本地执行状态");
        getSystemService(NotificationManager.class).createNotificationChannel(channel);
    }

    private Notification notification(String text) {
        return new Notification.Builder(this, CHANNEL)
                .setContentTitle("Jarvis Mobile")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.ic_lock_idle_alarm)
                .setOngoing(true).build();
    }
}
