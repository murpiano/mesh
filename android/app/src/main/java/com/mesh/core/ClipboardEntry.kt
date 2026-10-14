package com.mesh.core

import androidx.room.Entity
import androidx.room.PrimaryKey

@Entity(tableName = "clipboard_history")
data class ClipboardEntry(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val text: String,
    val source: String,
    val timestamp: Long
)
