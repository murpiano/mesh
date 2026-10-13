package com.mesh.core

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase

@Database(entities = [PairedDevice::class], version = 1)
abstract class MeshDatabase : RoomDatabase() {
    abstract fun pairedDeviceDao(): PairedDeviceDao

    companion object {
        @Volatile private var instance: MeshDatabase? = null

        fun getInstance(context: Context): MeshDatabase =
            instance ?: synchronized(this) {
                instance ?: Room.databaseBuilder(context, MeshDatabase::class.java, "mesh.db")
                    .build()
                    .also { instance = it }
            }
    }
}
