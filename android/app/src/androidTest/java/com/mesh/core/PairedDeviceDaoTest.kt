package com.mesh.core

import androidx.room.Room
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PairedDeviceDaoTest {
    private fun inMemoryDb(): MeshDatabase {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        return Room.inMemoryDatabaseBuilder(context, MeshDatabase::class.java).build()
    }

    @Test
    fun findByUuid_returnsNull_whenAbsent() = runBlocking {
        val db = inMemoryDb()
        assertNull(db.pairedDeviceDao().findByUuid("missing"))
    }

    @Test
    fun upsert_thenFindByUuid_roundTrips() = runBlocking {
        val db = inMemoryDb()
        val device = PairedDevice(
            uuid = "d1", name = "Pixel", fingerprint = "fp1",
            lastIp = "192.168.1.5", pairedAt = 1760000000000L
        )
        db.pairedDeviceDao().upsert(device)
        val loaded = db.pairedDeviceDao().findByUuid("d1")
        assertEquals("fp1", loaded?.fingerprint)
    }

    @Test
    fun upsert_replacesExistingUuid_ratherThanDuplicating() = runBlocking {
        val db = inMemoryDb()
        db.pairedDeviceDao().upsert(PairedDevice("d1", "Old", "fp1", "1.1.1.1", 1L))
        db.pairedDeviceDao().upsert(PairedDevice("d1", "New", "fp2", "2.2.2.2", 2L))
        val all = db.pairedDeviceDao().all()
        assertEquals(1, all.size)
        assertEquals("fp2", all[0].fingerprint)
    }
}
