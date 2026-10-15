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

class ChatMessageDaoTest {
    private lateinit var db: MeshDatabase
    private lateinit var dao: ChatMessageDao

    @Before
    fun setUp() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        db = Room.inMemoryDatabaseBuilder(context, MeshDatabase::class.java).build()
        dao = db.chatMessageDao()
    }

    @After
    fun tearDown() { db.close() }

    private fun message(msgId: String, peerUuid: String, text: String, timestamp: Long) =
        ChatMessage(msgId, peerUuid, "self-uuid", "You", text, timestamp)

    @Test
    fun insertAndEvict_thenGetAllForPeer_roundTrips() = runBlocking {
        dao.insertAndEvict(message("msg-1", "peer-a", "hello", 1000L))
        val all = dao.getAllForPeer("peer-a")
        assertEquals(1, all.size)
        assertEquals("hello", all[0].text)
        assertEquals("You", all[0].senderLabel)
    }

    @Test
    fun insertAndEvict_duplicateMsgId_returnsFalse_isIgnored() = runBlocking {
        val inserted = dao.insertAndEvict(message("msg-1", "peer-a", "hello", 1000L))
        val duplicate = dao.insertAndEvict(message("msg-1", "peer-a", "hello again", 1000L))

        assertTrue(inserted)
        assertFalse(duplicate)
        val all = dao.getAllForPeer("peer-a")
        assertEquals(1, all.size)
        assertEquals("hello", all[0].text) // first write wins
    }

    @Test
    fun getAllForPeer_returnsOldestFirst() = runBlocking {
        dao.insertAndEvict(message("msg-1", "peer-a", "first", 1000L))
        dao.insertAndEvict(message("msg-2", "peer-a", "second", 2000L))
        val all = dao.getAllForPeer("peer-a")
        assertEquals("first", all[0].text)
        assertEquals("second", all[1].text)
    }

    @Test
    fun getAllForPeer_scopedToPeer_otherPeerMessagesExcluded() = runBlocking {
        dao.insertAndEvict(message("msg-a", "peer-a", "to a", 1000L))
        dao.insertAndEvict(message("msg-b", "peer-b", "to b", 1000L))

        assertEquals(listOf("to a"), dao.getAllForPeer("peer-a").map { it.text })
        assertEquals(listOf("to b"), dao.getAllForPeer("peer-b").map { it.text })
    }

    @Test
    fun insertBeyond50ForSamePeer_evictsOldest_countStaysAt50() = runBlocking {
        for (i in 0 until 51) {
            dao.insertAndEvict(message("msg-$i", "peer-a", "entry-$i", i.toLong()))
        }
        val all = dao.getAllForPeer("peer-a")
        assertEquals(50, all.size)
        assertFalse(all.any { it.text == "entry-0" })
        assertTrue(all.any { it.text == "entry-50" })
    }

    @Test
    fun insertBeyond50ForOnePeer_doesNotEvictAnotherPeersMessages() = runBlocking {
        dao.insertAndEvict(message("msg-b", "peer-b", "to b", 1000L))
        for (i in 0 until 51) {
            dao.insertAndEvict(message("msg-a-$i", "peer-a", "entry-$i", i.toLong()))
        }
        assertEquals(listOf("to b"), dao.getAllForPeer("peer-b").map { it.text })
        assertEquals(50, dao.getAllForPeer("peer-a").size)
    }

    @Test
    fun deleteForPeer_removesOnlyThatPeersMessages() = runBlocking {
        dao.insertAndEvict(message("msg-a", "peer-a", "to a", 1000L))
        dao.insertAndEvict(message("msg-b", "peer-b", "to b", 1000L))

        dao.deleteForPeer("peer-a")

        assertTrue(dao.getAllForPeer("peer-a").isEmpty())
        assertEquals(listOf("to b"), dao.getAllForPeer("peer-b").map { it.text })
    }
}
