package com.jarvis.mobile;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public final class PolicyExpiryReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        MobilePolicy policy = PolicyStore.read(context);
        if (policy != null && policy.endEpoch <= System.currentTimeMillis())
            PolicyStore.clear(context, "expiry-alarm");
    }
}
