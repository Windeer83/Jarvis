package com.jarvis.mobile;

import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public final class PolicyRulesTest {
    @Test public void activeTargetIsBlockedImmediately() {
        assertEquals(PolicyRules.Decision.BLOCK,
                PolicyRules.decide(true, true, true, false));
    }

    @Test public void missingMechanismIsExplicitlyUnavailable() {
        assertEquals(PolicyRules.Decision.UNAVAILABLE,
                PolicyRules.decide(true, false, true, false));
    }

    @Test public void temporaryAccessWinsForOnlyTheCurrentTarget() {
        assertEquals(PolicyRules.Decision.TEMPORARILY_ALLOWED,
                PolicyRules.decide(true, true, true, true));
    }

    @Test public void staleVersionCannotReplaceSameCommitment() {
        assertFalse(PolicyRules.shouldReplace("same", 3, "same", 2));
        assertFalse(PolicyRules.shouldReplace("same", 3, "same", 3));
        assertTrue(PolicyRules.shouldReplace("old", 9, "new", 1));
    }
}
