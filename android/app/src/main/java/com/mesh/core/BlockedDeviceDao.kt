package com.mesh.core

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query

/** The blacklist. Blocking is purely local — the peer is never told, it just stops getting
 * anywhere — so a device unblocked here is an ordinary unpaired device again. Mirrors the
 * desktop app's BlockedDeviceStore. */
@Dao
interface BlockedDeviceDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(device: BlockedDevice)

    @Query("SELECT * FROM blocked_devices ORDER BY name COLLATE NOCASE")
    suspend fun all(): List<BlockedDevice>

    @Query("SELECT EXISTS(SELECT 1 FROM blocked_devices WHERE uuid = :uuid)")
    suspend fun isBlocked(uuid: String): Boolean

    @Query("DELETE FROM blocked_devices WHERE uuid = :uuid")
    suspend fun deleteByUuid(uuid: String)
}
