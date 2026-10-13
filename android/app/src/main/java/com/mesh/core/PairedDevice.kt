package com.mesh.core

import androidx.room.Entity
import androidx.room.PrimaryKey

@Entity(tableName = "paired_devices")
data class PairedDevice(
    @PrimaryKey val uuid: String,
    val name: String,
    val fingerprint: String,
    val lastIp: String,
    val pairedAt: Long
)
