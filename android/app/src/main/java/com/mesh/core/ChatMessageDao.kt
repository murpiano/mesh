package com.mesh.core

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction

@Dao
interface ChatMessageDao {
    /** Inserts [message], or does nothing if its msgId is already stored (e.g. replayed back by
     * a history sync the receiver already has). Returns false on that duplicate case so callers
     * know not to add a second copy to the UI. */
    @Transaction
    suspend fun insertAndEvict(message: ChatMessage): Boolean {
        if (insert(message) == -1L) return false // msgId already present
        evictPast(message.peerUuid, CAP)
        return true
    }

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insert(message: ChatMessage): Long

    @Query(
        """
        DELETE FROM chat_messages
        WHERE peerUuid = :peerUuid AND msgId NOT IN (
            SELECT msgId FROM chat_messages WHERE peerUuid = :peerUuid
            ORDER BY timestamp DESC, msgId DESC LIMIT :cap
        )
        """
    )
    suspend fun evictPast(peerUuid: String, cap: Int)

    /** All messages for one conversation, oldest first (chat order). */
    @Query("SELECT * FROM chat_messages WHERE peerUuid = :peerUuid ORDER BY timestamp ASC, msgId ASC")
    suspend fun getAllForPeer(peerUuid: String): List<ChatMessage>

    /** Removes the whole conversation with [peerUuid] — called when the user unpairs that
     * device (see spec: unpairing loses the chat too, same as the trust record; the peer's own
     * copy is untouched and restores ours on a later re-pair). */
    @Query("DELETE FROM chat_messages WHERE peerUuid = :peerUuid")
    suspend fun deleteForPeer(peerUuid: String)

    /** Edits one message's text in place. No-op if [msgId] is not stored for [peerUuid]. */
    @Query("UPDATE chat_messages SET text = :newText WHERE peerUuid = :peerUuid AND msgId = :msgId")
    suspend fun updateText(peerUuid: String, msgId: String, newText: String)

    /** Removes one message. No-op if [msgId] is not stored for [peerUuid]. */
    @Query("DELETE FROM chat_messages WHERE peerUuid = :peerUuid AND msgId = :msgId")
    suspend fun deleteMessage(peerUuid: String, msgId: String)

    companion object {
        /** Messages kept per conversation (per peerUuid), not globally — each paired device
         * gets its own 50-message window. */
        const val CAP = 50
    }
}
