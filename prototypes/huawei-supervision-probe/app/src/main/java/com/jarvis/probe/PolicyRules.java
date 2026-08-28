package com.jarvis.probe;

/**
 * Pure decision surface for the question under test. Android I/O stays outside this class so the
 * measured rule can be lifted into the production core if the device mechanism passes.
 */
final class PolicyRules {
    enum Decision {
        INACTIVE,
        UNAVAILABLE,
        OBSERVE,
        TEMPORARILY_ALLOWED,
        BLOCK
    }

    private PolicyRules() {
    }

    static Decision decide(
            boolean policyActive,
            boolean mechanismAvailable,
            boolean targetForeground,
            boolean temporaryAccessActive
    ) {
        if (!policyActive) {
            return Decision.INACTIVE;
        }
        if (!mechanismAvailable) {
            return Decision.UNAVAILABLE;
        }
        if (!targetForeground) {
            return Decision.OBSERVE;
        }
        if (temporaryAccessActive) {
            return Decision.TEMPORARILY_ALLOWED;
        }
        return Decision.BLOCK;
    }
}
