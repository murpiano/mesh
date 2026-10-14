package com.mesh.core

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.Query
import androidx.room.Transaction

@Dao
interface ClipboardHistoryDao {
    @Transaction
    suspend fun insertAndEvict(entry: ClipboardEntry) {
        insert(entry)
        evictPast(CAP)
    }

    @Insert
    suspend fun insert(entry: ClipboardEntry)

    @Query(
        """
        DELETE FROM clipboard_history
        WHERE id NOT IN (
            SELECT id FROM clipboard_history ORDER BY timestamp DESC, id DESC LIMIT :cap
        )
        """
    )
    suspend fun evictPast(cap: Int)

    @Query("SELECT * FROM clipboard_history ORDER BY timestamp DESC, id DESC")
    suspend fun getAll(): List<ClipboardEntry>

    companion object {
        const val CAP = 50
    }
}
