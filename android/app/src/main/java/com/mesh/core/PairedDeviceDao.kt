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
}
