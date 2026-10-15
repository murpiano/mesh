package com.mesh.core

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query

@Dao
interface PairedDeviceDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(device: PairedDevice)

    @Query("SELECT * FROM paired_devices WHERE uuid = :uuid LIMIT 1")
    suspend fun findByUuid(uuid: String): PairedDevice?

    @Query("SELECT * FROM paired_devices")
    suspend fun all(): List<PairedDevice>

    /** Removes the local trust record for [uuid], if any. Local-only: the peer's own record of
     * us is untouched, so it still shows us as paired until it next tries to connect and our
     * (now-missing) record forces a fresh PIN pairing. */
    @Query("DELETE FROM paired_devices WHERE uuid = :uuid")
    suspend fun deleteByUuid(uuid: String)
}
