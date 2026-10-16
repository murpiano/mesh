package com.mesh.core

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * One chat message, scoped to the conversation with [peerUuid]. [msgId] is a globally unique id
 * assigned at creation (by whichever side wrote the message), used to dedupe during a
 * [ChatHistorySync] merge. [senderUuid] is a device uuid (either side's) rather than a
 * perspective-relative label, so attribution survives being replayed through a history sync.
 */
@Entity(tableName = "chat_messages")
data class ChatMessage(
    @PrimaryKey val msgId: String,
    val peerUuid: String,
    val senderUuid: String,
    val senderLabel: String,
    val text: String,
    val timestamp: Long
) {
    companion object {
        /** Longest [text] prefix the history list UI will render directly. */
        const val DISPLAY_TEXT_MAX_LENGTH = 500
    }

    /**
     * [text] truncated to [DISPLAY_TEXT_MAX_LENGTH] characters (plus an ellipsis) for list-view
     * rendering. Messages can be up to 4 MiB of text; rendering that much through a wrapping
     * TextView with no cap can jank the UI thread, so the view binds to this instead of [text].
     * The "Copy" action still uses the full, untruncated [text].
     */
    val displayText: String
        get() = if (text.length > DISPLAY_TEXT_MAX_LENGTH) text.take(DISPLAY_TEXT_MAX_LENGTH) + "…" else text
}
