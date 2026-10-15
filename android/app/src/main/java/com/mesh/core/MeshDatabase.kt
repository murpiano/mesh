package com.mesh.core

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

@Database(entities = [PairedDevice::class, ChatMessage::class], version = 3)
abstract class MeshDatabase : RoomDatabase() {
    abstract fun pairedDeviceDao(): PairedDeviceDao
    abstract fun chatMessageDao(): ChatMessageDao

    companion object {
        @Volatile private var instance: MeshDatabase? = null

        /**
         * v1 -> v2 only adds the clipboard_history table (see the old ClipboardEntry). Real
         * devices from sub-project 1 already have paired_devices rows from real-hardware
         * pairing; a destructive migration would wipe that trust store and force a full PIN
         * re-pair on every existing install, so this must be an additive migration instead.
         */
        val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS clipboard_history (
                        id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                        text TEXT NOT NULL,
                        source TEXT NOT NULL,
                        timestamp INTEGER NOT NULL
                    )
                    """.trimIndent()
                )
            }
        }

        /**
         * v2 -> v3: the clipboard-sync feature became a chat feature — scoped per paired device
         * (peerUuid) and synced on connect, instead of one global flat history. The old
         * clipboard_history table's rows have no peerUuid and no semantic meaning as chat
         * messages, so this drops it rather than attempting to migrate; paired_devices (the
         * trust store) is untouched.
         */
        val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("DROP TABLE IF EXISTS clipboard_history")
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS chat_messages (
                        msgId TEXT NOT NULL PRIMARY KEY,
                        peerUuid TEXT NOT NULL,
                        senderUuid TEXT NOT NULL,
                        senderLabel TEXT NOT NULL,
                        text TEXT NOT NULL,
                        timestamp INTEGER NOT NULL
                    )
                    """.trimIndent()
                )
            }
        }

        fun getInstance(context: Context): MeshDatabase =
            instance ?: synchronized(this) {
                instance ?: Room.databaseBuilder(context, MeshDatabase::class.java, "mesh.db")
                    .addMigrations(MIGRATION_1_2, MIGRATION_2_3)
                    .build()
                    .also { instance = it }
            }
    }
}
