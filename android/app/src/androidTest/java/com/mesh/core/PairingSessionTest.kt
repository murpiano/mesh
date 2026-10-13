package com.mesh.core

import androidx.room.Room
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class PairingSessionTest {
    @Test
    fun handshake_bothSidesComputeSamePin() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val identityA = IdentityStore.loadOrCreate(context)
        val identityB = IdentityStore.loadOrCreate(context) // same alias in this test env; real devices differ per-install

        val port = 51234
        val server = PairingSession()
        val serverJob = async { server.startListener(identityA, port) }

        delay(200)
        val client = PairingSession()
        client.connectTo(identityB, "127.0.0.1", port)
        serverJob.await()

        val serverPin = server.computePin(identityA.first)
        val clientPin = client.computePin(identityB.first)
        assertEquals(serverPin, clientPin)
    }

    @Test
    fun isPeerTrusted_returnsFalse_whenNoStoredRecord() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val db = Room.inMemoryDatabaseBuilder(context, MeshDatabase::class.java).build()
        val session = PairingSession()
        session.peerFingerprint = "some-fingerprint"
        assertFalse(session.isPeerTrusted(db.pairedDeviceDao(), "unknown-uuid"))
    }

    @Test
    fun isPeerTrusted_returnsFalse_whenFingerprintChanged() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val db = Room.inMemoryDatabaseBuilder(context, MeshDatabase::class.java).build()
        db.pairedDeviceDao().upsert(PairedDevice("known-uuid", "Old", "old-fingerprint", "1.2.3.4", 1L))

        val session = PairingSession()
        session.peerFingerprint = "new-fingerprint"
        assertFalse(session.isPeerTrusted(db.pairedDeviceDao(), "known-uuid"))
    }
}
