package com.mesh.core

import androidx.room.Entity
import androidx.room.PrimaryKey

/** One device the user has blacklisted: it is kept out of the device list and every connection
 * it attempts is hung up on. Mirrors the desktop app's BlockedDevice. */
@Entity(tableName = "blocked_devices")
data class BlockedDevice(
    @PrimaryKey val uuid: String,
    val name: String,
    val blockedAt: Long
)
