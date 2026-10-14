package com.mesh.core

import androidx.room.Room
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class ClipboardHistoryDaoTest {
    private lateinit var db: MeshDatabase
    private lateinit var dao: ClipboardHistoryDao

    @Before
    fun setUp() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        db = Room.inMemoryDatabaseBuilder(context, MeshDatabase::class.java).build()
        dao = db.clipboardHistoryDao()
    }

    @After
    fun tearDown() { db.close() }

    @Test
    fun insertAndEvict_thenGetAll_roundTrips() = runBlocking {
        dao.insertAndEvict(ClipboardEntry(text = "hello", source = "local", timestamp = 1000L))
        val all = dao.getAll()
        assertEquals(1, all.size)
        assertEquals("hello", all[0].text)
        assertEquals("local", all[0].source)
    }

    @Test
    fun getAll_returnsNewestFirst() = runBlocking {
        dao.insertAndEvict(ClipboardEntry(text = "first", source = "local", timestamp = 1000L))
        dao.insertAndEvict(ClipboardEntry(text = "second", source = "local", timestamp = 2000L))
        val all = dao.getAll()
        assertEquals("second", all[0].text)
        assertEquals("first", all[1].text)
    }

    @Test
    fun insertBeyond50_evictsOldest_countStaysAt50() = runBlocking {
        for (i in 0 until 51) {
            dao.insertAndEvict(ClipboardEntry(text = "entry-$i", source = "local", timestamp = i.toLong()))
        }
        val all = dao.getAll()
        assertEquals(50, all.size)
        assertFalse(all.any { it.text == "entry-0" })
        assertTrue(all.any { it.text == "entry-50" })
    }
}
