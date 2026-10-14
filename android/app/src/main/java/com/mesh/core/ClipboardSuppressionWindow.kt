package com.mesh.core

/**
 * Tracks the short "ignore the next clipboard-change notification" window that [ClipboardWatcher]
 * uses when the app writes to the clipboard itself (e.g. a "Copy" button on a history entry), so
 * that write doesn't get re-detected and re-pushed to the peer as if it were a brand-new local
 * copy.
 *
 * Pulled out of [ClipboardWatcher] into its own pure-Kotlin class, with an injectable clock,
 * purely so this timing decision can be unit-tested directly without needing a real
 * [android.content.ClipboardManager] (which requires Robolectric/instrumentation to construct).
 */
class ClipboardSuppressionWindow(private val clock: () -> Long = System::currentTimeMillis) {
    private var suppressUntilMs = 0L

    /** Call right before the app writes to the clipboard itself so the resulting change
     * notification is suppressed for [windowMs] milliseconds. */
    fun suppressNextChange(windowMs: Long) {
        suppressUntilMs = clock() + windowMs
    }

    /** True if a clipboard-change notification arriving right now falls within an active
     * suppression window set by [suppressNextChange] and should be ignored. */
    fun shouldSuppress(): Boolean = clock() < suppressUntilMs
}
