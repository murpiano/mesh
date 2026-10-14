package com.mesh.core

import android.content.ClipboardManager
import android.content.Context

/**
 * Fires [onTextCopied] when the Android primary clipboard's text content changes.
 *
 * Platform note: since Android 10 the change listener is only delivered while the app has
 * input focus (foreground), so copies made in other apps are seen when they happen while Mesh
 * is in front, not in the background.
 */
class ClipboardWatcher(context: Context) {
    private val clipboardManager = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    private val suppression = ClipboardSuppressionWindow()

    var onTextCopied: ((String) -> Unit)? = null

    private val listener = ClipboardManager.OnPrimaryClipChangedListener {
        if (suppression.shouldSuppress()) return@OnPrimaryClipChangedListener
        val clip = clipboardManager.primaryClip
        if (clip != null && clip.itemCount > 0) {
            val text = clip.getItemAt(0).text?.toString()
            if (!text.isNullOrEmpty()) onTextCopied?.invoke(text)
        }
    }

    fun start() {
        clipboardManager.addPrimaryClipChangedListener(listener)
    }

    fun stop() {
        clipboardManager.removePrimaryClipChangedListener(listener)
    }

    /** Call right before the app writes to the clipboard itself (e.g. a "Copy" button on a
     * history entry) so the resulting change notification isn't re-pushed. */
    fun suppressNextChange(windowMs: Long) {
        suppression.suppressNextChange(windowMs)
    }
}
