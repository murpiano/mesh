package com.mesh.core

import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.net.Socket
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.X509Certificate

class FakeDao : PairedDeviceDao {
    private val rows = mutableMapOf<String, PairedDevice>()
    override suspend fun upsert(device: PairedDevice) = synchronized(rows) { rows[device.uuid] = device }
    override suspend fun findByUuid(uuid: String): PairedDevice? = synchronized(rows) { rows[uuid] }
    override suspend fun all(): List<PairedDevice> = synchronized(rows) { rows.values.toList() }
    override suspend fun deleteByUuid(uuid: String) = synchronized(rows) { rows.remove(uuid); Unit }
}

/**
 * Plays either pairing role. As a listener prompt ([confirmPin]), [pinAnswer] is the "Match"/
 * "Not matching" verdict. As a dialer prompt ([enterPin]), there is no PIN to compare visually —
 * [pinAnswer] instead means "type the correct code", which requires [session] (the dialer's own
 * [PairingSession]) so this can read back the same value [PairingSession.computePin] will check
 * against; false means "type something wrong" (modeled as null — doesn't match).
 */
class FakePrompt(
    private val pinAnswer: Boolean,
    private val certChangedAnswer: Boolean = true,
    private val hang: Boolean = false,
    private val session: PairingSession? = null
) : PairingPrompt {
    @Volatile var shownPin: String? = null
    @Volatile var certChangedShown = false
    @Volatile var cancelled = false

    override suspend fun confirmCertificateChanged(peerName: String): Boolean {
        certChangedShown = true
        if (hang) hangUntilCancelled()
        return certChangedAnswer
    }

    override suspend fun confirmPin(peerName: String, pin: String): Boolean {
        shownPin = pin
        if (hang) hangUntilCancelled()
        return pinAnswer
    }

    override suspend fun enterPin(peerName: String): String? {
        if (hang) hangUntilCancelled()
        return if (pinAnswer) session?.computePin() else null
    }

    override suspend fun confirmReconnect(peerName: String): Boolean = pinAnswer

    private suspend fun hangUntilCancelled(): Nothing {
        try {
            awaitCancellation()
        } finally {
            cancelled = true
        }
    }
}

/**
 * JVM (non-instrumented) tests of the real TLS handshake + pairing protocol over loopback,
 * using two DISTINCT software identities loaded from PKCS12 fixtures (src/test/resources).
 * The Keystore-backed path is covered by the instrumented PairingSessionTest.
 */
class PairingSessionLoopbackTest {
    private val idA = loadFixture("identity_a.p12")
    private val idB = loadFixture("identity_b.p12")

    private fun loadFixture(name: String): Pair<X509Certificate, PrivateKey> {
        val ks = KeyStore.getInstance("PKCS12")
        javaClass.getResourceAsStream("/$name")!!.use { ks.load(it, "testpass".toCharArray()) }
        return Pair(
            ks.getCertificate("mesh") as X509Certificate,
            ks.getKey("mesh", "testpass".toCharArray()) as PrivateKey)
    }

    private suspend fun connectPair(
        server: Pair<X509Certificate, PrivateKey>,
        client: Pair<X509Certificate, PrivateKey>,
        protocols: Array<String>? = null
    ): Pair<PairingSession, PairingSession> = coroutineScope {
        PairingListener(server, 0).use { listener ->
            val accepted = async { listener.accept() }
            val c = PairingSession.connect(client, "127.0.0.1", listener.port, protocols)
            Pair(accepted.await(), c)
        }
    }

    @Test
    fun listener_bindsEphemeralPort_whenGivenZero() {
        PairingListener(idA, 0).use { assertTrue(it.port in 1..65535) }
    }

    @Test
    fun handshake_bothSidesComputeSamePin_withDistinctIdentities() = runBlocking {
        for (protocols in listOf(arrayOf("TLSv1.2"), arrayOf("TLSv1.3"))) {
            val (server, client) = connectPair(idA, idB, protocols)
            server.use {
                client.use {
                    assertNotEquals(server.peerFingerprint, client.peerFingerprint)
                    assertEquals(IdentityStore.fingerprint(idB.first), server.peerFingerprint)
                    listOf(
                        async { server.exchangeIdentity("uuid-a", "Alpha") },
                        async { client.exchangeIdentity("uuid-b", "Bravo") }).awaitAll()
                    assertEquals("uuid-b", server.peerUuid)
                    assertEquals("Bravo", server.peerName)
                    assertEquals("uuid-a", client.peerUuid)
                    assertEquals("Alpha", client.peerName)

                    listOf(async { server.exchangeNonces() }, async { client.exchangeNonces() }).awaitAll()
                    assertEquals(server.computePin(), client.computePin())
                }
            }
        }
    }

    @Test
    fun accept_throws_whenPeerNeverCompletesTlsHandshake() = runBlocking {
        PairingListener(idA, 0).use { listener ->
            Socket("127.0.0.1", listener.port).use {
                // Raw TCP connect, never send a ClientHello: bounded by the 10 s SO_TIMEOUT.
                val started = System.currentTimeMillis()
                try {
                    listener.accept()
                    fail("expected handshake timeout")
                } catch (e: Exception) {
                    val elapsed = System.currentTimeMillis() - started
                    assertTrue("elapsed=$elapsed", elapsed in 9_000..20_000)
                }
            }
        }
    }

    @Test
    fun acceptLoop_keepsListening_afterFailedAttempt() = runBlocking {
        PairingListener(idA, 0).use { listener ->
            val errors = mutableListOf<Exception>()
            val handled = kotlinx.coroutines.CompletableDeferred<String>()
            val loop = launch {
                listener.runAcceptLoop({ handled.complete(it.peerFingerprint) }, { synchronized(errors) { errors.add(it) } })
            }
            // 1st attempt: connect and hang up immediately.
            Socket("127.0.0.1", listener.port).close()
            // 2nd attempt on the same port must still be served.
            PairingSession.connect(idB, "127.0.0.1", listener.port).use {
                assertEquals(IdentityStore.fingerprint(idB.first), withTimeout(15_000) { handled.await() })
            }
            assertTrue(synchronized(errors) { errors.isNotEmpty() })
            listener.close()
            withTimeout(5_000) { loop.join() }
        }
    }

    @Test
    fun run_bothConfirm_bothSidesStorePeerRecord() = runBlocking {
        val daoA = FakeDao()
        val daoB = FakeDao()
        val promptA = FakePrompt(true)
        val (server, client) = connectPair(idA, idB)
        val promptB = FakePrompt(true, session = client)
        server.use {
            client.use {
                val results = listOf(
                    async { server.run("uuid-a", "Alpha", daoA, promptA) },
                    async { client.run("uuid-b", "Bravo", daoB, promptB) }).awaitAll()
                results.forEach { assertEquals(PairingResult.PAIRED, it.result) }
            }
        }
        assertFalse(promptA.certChangedShown)

        val recA = daoA.all().single()
        assertEquals("uuid-b", recA.uuid)
        assertEquals("Bravo", recA.name)
        assertEquals(IdentityStore.fingerprint(idB.first), recA.fingerprint)
        assertEquals("127.0.0.1", recA.lastIp)
        val recB = daoB.all().single()
        assertEquals("uuid-a", recB.uuid)
        assertEquals("Alpha", recB.name)
        assertEquals(IdentityStore.fingerprint(idA.first), recB.fingerprint)
    }

    @Test
    fun run_anyReject_bothFail_andNeitherWritesRecord() = runBlocking {
        for ((answerA, answerB) in listOf(false to true, true to false, false to false)) {
            val daoA = FakeDao()
            val daoB = FakeDao()
            val (server, client) = connectPair(idA, idB)
            server.use {
                client.use {
                    val results = listOf(
                        async { server.run("uuid-a", "Alpha", daoA, FakePrompt(answerA)) },
                        async { client.run("uuid-b", "Bravo", daoB, FakePrompt(answerB, session = client)) }).awaitAll()
                    results.forEach { assertEquals("$answerA/$answerB", PairingResult.PAIRING_FAILED, it.result) }
                }
            }
            assertTrue(daoA.all().isEmpty())
            assertTrue(daoB.all().isEmpty())
        }
    }

    @Test
    fun run_userNeverAnswers_bothFailAfterTimeout_dialogDismissed_noRecord() = runBlocking {
        val daoA = FakeDao()
        val daoB = FakeDao()
        val hangingPrompt = FakePrompt(true, hang = true)
        val (server, client) = connectPair(idA, idB)
        server.userTimeoutMs = 1_000
        client.userTimeoutMs = 1_000
        server.use {
            client.use {
                val results = withTimeout(15_000) {
                    listOf(
                        async { server.run("uuid-a", "Alpha", daoA, hangingPrompt) },
                        async { client.run("uuid-b", "Bravo", daoB, FakePrompt(true)) }).awaitAll()
                }
                results.forEach { assertEquals(PairingResult.PAIRING_FAILED, it.result) }
            }
        }
        assertTrue(hangingPrompt.cancelled)
        assertTrue(daoA.all().isEmpty())
        assertTrue(daoB.all().isEmpty())
    }

    // The old run_peerRejectsEarly_dismissesLocalDialog tested the symmetric race's "peer
    // answers quickly, dismisses our hung dialog" path. The asymmetric protocol has no dialer-
    // side equivalent (the dialer never answers until after the listener does), and the
    // listener-side equivalent — a vanished peer dismissing our hung dialog — is already
    // covered below by run_peerDisconnectsBeforeConfirming_isPairingFailed_noRecord.

    @Test
    fun run_peerDisconnectsBeforeConfirming_isPairingFailed_noRecord() = runBlocking {
        val daoA = FakeDao()
        val (server, client) = connectPair(idA, idB)
        server.use {
            val serverRun = async { server.run("uuid-a", "Alpha", daoA, FakePrompt(true, hang = true)) }
            client.exchangeIdentity("uuid-b", "Bravo")
            client.exchangeManualIntent(false)
            client.exchangeTrust(false)
            client.exchangeNonces()
            client.close() // vanish while A's PIN dialog is up
            assertEquals(PairingResult.PAIRING_FAILED, withTimeout(15_000) { serverRun.await() }.result)
        }
        assertTrue(daoA.all().isEmpty())
    }

    @Test
    fun run_secondConnection_isTrustedReconnect_inBothDirections() = runBlocking {
        val daoA = FakeDao()
        val daoB = FakeDao()
        val (s1, c1) = connectPair(idA, idB)
        s1.use { c1.use {
            listOf(
                async { s1.run("uuid-a", "Alpha", daoA, FakePrompt(true)) },
                async { c1.run("uuid-b", "Bravo", daoB, FakePrompt(true, session = c1)) }).awaitAll()
        } }

        // Roles swapped: A initiates, B is now the listener.
        val promptA = FakePrompt(false)
        val promptB = FakePrompt(false)
        val (s2, c2) = connectPair(idB, idA)
        s2.use { c2.use {
            val results = listOf(
                async { s2.run("uuid-b", "Bravo", daoB, promptB) },
                async { c2.run("uuid-a", "Alpha", daoA, promptA) }).awaitAll()
            results.forEach { assertEquals(PairingResult.RECONNECTED, it.result) }
        } }
        assertNull(promptA.shownPin)
        assertNull(promptB.shownPin)
    }

    @Test
    fun run_onlyOneSideTrusts_bothFallBackToPinFlow() = runBlocking {
        val daoA = FakeDao()
        val daoB = FakeDao()
        daoA.upsert(PairedDevice("uuid-b", "Bravo", IdentityStore.fingerprint(idB.first), "10.0.0.9", 1L))
        val promptA = FakePrompt(true)
        val (server, client) = connectPair(idA, idB)
        val promptB = FakePrompt(true, session = client)
        server.use { client.use {
            val results = listOf(
                async { server.run("uuid-a", "Alpha", daoA, promptA) },
                async { client.run("uuid-b", "Bravo", daoB, promptB) }).awaitAll()
            results.forEach { assertEquals(PairingResult.PAIRED, it.result) }
        } }
        assertTrue(promptA.shownPin != null)
        assertEquals(1, daoB.all().size)
    }

    @Test
    fun run_certificateChanged_warnsFirst_andDeclineFailsWithoutOverwriting() = runBlocking {
        val daoA = FakeDao()
        val daoB = FakeDao()
        daoA.upsert(PairedDevice("uuid-b", "Bravo", "old-fingerprint", "10.0.0.9", 1L))
        val promptA = FakePrompt(pinAnswer = true, certChangedAnswer = false)
        val (server, client) = connectPair(idA, idB)
        server.use { client.use {
            val results = listOf(
                async { server.run("uuid-a", "Alpha", daoA, promptA) },
                async { client.run("uuid-b", "Bravo", daoB, FakePrompt(true)) }).awaitAll()
            results.forEach { assertEquals(PairingResult.PAIRING_FAILED, it.result) }
        } }
        assertTrue(promptA.certChangedShown)
        assertNull(promptA.shownPin)
        assertEquals("old-fingerprint", daoA.findByUuid("uuid-b")!!.fingerprint)
        assertTrue(daoB.all().isEmpty())
    }

    @Test
    fun run_certificateChanged_acceptThenMatch_rePairsWithNewFingerprint() = runBlocking {
        val daoA = FakeDao()
        daoA.upsert(PairedDevice("uuid-b", "Bravo", "old-fingerprint", "10.0.0.9", 1L))
        val promptA = FakePrompt(pinAnswer = true, certChangedAnswer = true)
        val (server, client) = connectPair(idA, idB)
        server.use { client.use {
            val results = listOf(
                async { server.run("uuid-a", "Alpha", daoA, promptA) },
                async { client.run("uuid-b", "Bravo", FakeDao(), FakePrompt(true, session = client)) }).awaitAll()
            results.forEach { assertEquals(PairingResult.PAIRED, it.result) }
        } }
        assertTrue(promptA.certChangedShown)
        assertEquals(IdentityStore.fingerprint(idB.first), daoA.findByUuid("uuid-b")!!.fingerprint)
    }

    @Test
    fun checkTrust_distinguishesUnknownTrustedAndChanged() = runBlocking {
        val dao = FakeDao()
        assertEquals(PeerTrust.UNKNOWN, PairingSession.checkTrust(dao, "known-uuid", "fp"))
        dao.upsert(PairedDevice("known-uuid", "Old", "old-fingerprint", "1.2.3.4", 1L))
        assertEquals(PeerTrust.TRUSTED, PairingSession.checkTrust(dao, "known-uuid", "old-fingerprint"))
        assertEquals(PeerTrust.CERTIFICATE_CHANGED, PairingSession.checkTrust(dao, "known-uuid", "new-fingerprint"))
        assertEquals(PeerTrust.UNKNOWN, PairingSession.checkTrust(dao, "other-uuid", "old-fingerprint"))
    }

    @Test
    fun detachTransport_makesCloseNoOp_andLeavesSocketUsable() = runBlocking {
        val (server, client) = connectPair(idA, idB)
        val serverTransport = server.detachTransport()
        val clientTransport = client.detachTransport()
        try {
            server.close() // must now be a no-op
            client.close() // must now be a no-op

            assertFalse(serverTransport.socket.isClosed)
            assertFalse(clientTransport.socket.isClosed)

            // Prove the detached streams still carry protocol bytes end-to-end, not just that the socket object exists.
            val payload = "hello".toByteArray()
            clientTransport.output.write(payload)
            clientTransport.output.flush()
            val received = ByteArray(payload.size)
            serverTransport.input.readFully(received)
            assertEquals("hello", String(received))
        } finally {
            serverTransport.socket.close()
            clientTransport.socket.close()
        }
    }
}
