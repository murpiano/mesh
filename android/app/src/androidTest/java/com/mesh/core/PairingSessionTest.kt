package com.mesh.core

import androidx.room.Room
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * On-device validation of the real TLS handshake with two DISTINCT AndroidKeyStore-backed
 * identities (separate aliases), for both TLS 1.2 and TLS 1.3. This is the test that proves
 * the Keystore key authorizations in IdentityStore.tlsKeySpec actually work with Conscrypt.
 */
class PairingSessionTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val aliasA = "mesh_test_identity_a"
    private val aliasB = "mesh_test_identity_b"

    @After
    fun cleanup() {
        IdentityStore.delete(aliasA)
        IdentityStore.delete(aliasB)
    }

    private fun handshakeAndCompare(protocols: Array<String>) = runBlocking {
        val identityA = IdentityStore.loadOrCreate(context, aliasA)
        val identityB = IdentityStore.loadOrCreate(context, aliasB)
        assertNotEquals(IdentityStore.fingerprint(identityA.first), IdentityStore.fingerprint(identityB.first))

        PairingListener(identityA, 0).use { listener ->
            val accepted = async { listener.accept() }
            val client = PairingSession.connect(identityB, "127.0.0.1", listener.port, protocols)
            val server = accepted.await()
            server.use {
                client.use {
                    assertEquals(IdentityStore.fingerprint(identityB.first), server.peerFingerprint)
                    assertEquals(IdentityStore.fingerprint(identityA.first), client.peerFingerprint)
                    listOf(
                        async { server.exchangeIdentity("uuid-a", "Alpha") },
                        async { client.exchangeIdentity("uuid-b", "Bravo") }).awaitAll()
                    listOf(async { server.exchangeNonces() }, async { client.exchangeNonces() }).awaitAll()
                    assertEquals(server.computePin(), client.computePin())
                }
            }
        }
    }

    @Test
    fun handshake_tls12_keystoreIdentities_bothSidesComputeSamePin() = handshakeAndCompare(arrayOf("TLSv1.2"))

    @Test
    fun handshake_tls13_keystoreIdentities_bothSidesComputeSamePin() = handshakeAndCompare(arrayOf("TLSv1.3"))

    @Test
    fun run_bothConfirm_storesPeerInRoom() = runBlocking {
        val identityA = IdentityStore.loadOrCreate(context, aliasA)
        val identityB = IdentityStore.loadOrCreate(context, aliasB)
        val dbA = Room.inMemoryDatabaseBuilder(context, MeshDatabase::class.java).build()
        val dbB = Room.inMemoryDatabaseBuilder(context, MeshDatabase::class.java).build()
        val yes = object : PairingPrompt {
            override suspend fun confirmCertificateChanged(peerName: String) = true
            override suspend fun confirmPin(peerName: String, pin: String) = true
        }

        PairingListener(identityA, 0).use { listener ->
            val accepted = async { listener.accept() }
            val client = PairingSession.connect(identityB, "127.0.0.1", listener.port)
            val server = accepted.await()
            server.use { client.use {
                val results = listOf(
                    async { server.run("uuid-a", "Alpha", dbA.pairedDeviceDao(), yes) },
                    async { client.run("uuid-b", "Bravo", dbB.pairedDeviceDao(), yes) }).awaitAll()
                results.forEach { assertEquals(PairingResult.PAIRED, it.result) }
            } }
        }
        assertEquals(IdentityStore.fingerprint(identityB.first), dbA.pairedDeviceDao().findByUuid("uuid-b")?.fingerprint)
        assertEquals(IdentityStore.fingerprint(identityA.first), dbB.pairedDeviceDao().findByUuid("uuid-a")?.fingerprint)
    }

    @Test
    fun checkTrust_distinguishesUnknownTrustedAndChanged() = runBlocking {
        val db = Room.inMemoryDatabaseBuilder(context, MeshDatabase::class.java).build()
        val dao = db.pairedDeviceDao()
        assertEquals(PeerTrust.UNKNOWN, PairingSession.checkTrust(dao, "known-uuid", "fp"))
        dao.upsert(PairedDevice("known-uuid", "Old", "old-fingerprint", "1.2.3.4", 1L))
        assertEquals(PeerTrust.TRUSTED, PairingSession.checkTrust(dao, "known-uuid", "old-fingerprint"))
        assertEquals(PeerTrust.CERTIFICATE_CHANGED, PairingSession.checkTrust(dao, "known-uuid", "new-fingerprint"))
    }
}
