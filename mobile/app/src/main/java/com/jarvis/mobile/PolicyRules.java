package com.jarvis.mobile;

final class PolicyRules {
    enum Decision { INACTIVE, UNAVAILABLE, OBSERVE, TEMPORARILY_ALLOWED, BLOCK }

    private PolicyRules() { }

    static Decision decide(boolean policyActive, boolean mechanismAvailable,
                           boolean targetForeground, boolean temporaryAccessActive) {
        if (!policyActive) return Decision.INACTIVE;
        if (!mechanismAvailable) return Decision.UNAVAILABLE;
        if (!targetForeground) return Decision.OBSERVE;
        if (temporaryAccessActive) return Decision.TEMPORARILY_ALLOWED;
        return Decision.BLOCK;
    }

    static boolean shouldReplace(String currentId, int currentVersion,
                                 String incomingId, int incomingVersion) {
        if (currentId == null) return true;
        if (!currentId.equals(incomingId)) return true;
        return incomingVersion > currentVersion;
    }
}
