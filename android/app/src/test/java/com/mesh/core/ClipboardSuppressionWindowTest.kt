package com.mesh.core

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers the self-copy dedup timing decision that [ClipboardWatcher] delegates to
 * [ClipboardSuppressionWindow]: a change notification that arrives within the suppression
 * window set by a "Copy" action must be suppressed, but one that arrives after the window has
 * expired (a genuinely new, different local copy made shortly afterwards) must not be eaten by
 * the dedup window.
 */
class ClipboardSuppressionWindowTest {
    @Test
    fun `shouldSuppress is false before any suppression is requested`() {
        val window = ClipboardSuppressionWindow()

        assertFalse(window.shouldSuppress())
    }

    @Test
    fun `shouldSuppress is true within the suppression window`() {
        var now = 1_000_000L
        val window = ClipboardSuppressionWindow { now }

        window.suppressNextChange(500L)
        // Simulate the OnPrimaryClipChangedListener callback firing 100ms later, still inside the window.
        now += 100L

        assertTrue(window.shouldSuppress())
    }

    @Test
    fun `shouldSuppress is false after the suppression window expires`() {
        var now = 1_000_000L
        val window = ClipboardSuppressionWindow { now }

        window.suppressNextChange(500L)
        // A genuinely new, different local copy made after the window has elapsed must not be
        // mistaken for the app's own write and swallowed.
        now += 600L

        assertFalse(window.shouldSuppress())
    }

    @Test
    fun `shouldSuppress using real clock suppresses then allows after real delay`() {
        // End-to-end check with the real wall clock (as ClipboardWatcher uses it in production),
        // to prove the injectable-clock tests above aren't hiding a wiring bug.
        val window = ClipboardSuppressionWindow()

        window.suppressNextChange(300L)
        assertTrue(window.shouldSuppress())

        Thread.sleep(400L)

        assertFalse(window.shouldSuppress())
    }
}
