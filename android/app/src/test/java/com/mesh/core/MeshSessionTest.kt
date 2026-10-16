package com.mesh.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.X509Certificate

/**
 * JVM tests of [MeshSession] over a real loopback TLS transport, built the same way
 * `PairingSessionLoopbackTest` builds its pairs (two distinct PKCS12 identities, real
 * handshake), then handed off via `PairingSession.detachTransport()`.
 *
 * Note: `PairingSessionLoopbackTest.connectPair` is a private helper of that test class, so
 * it can't be reused directly from here; [connectPair] below mirrors it exactly for this
 * file's own use.
 */
class MeshSessionTest {
    private val idA = loadFixture("identity_a.p12")
    private val idB = loadFixture("identity_b.p12")

    private fun loadFixture(name: String): Pair<X509Certificate, PrivateKey> {
        val ks = KeyStore.getInstance("PKCS12")
        javaClass.getResourceAsStream("/$name")!!.use { ks.load(it, "testpass".toCharArray()) }
        return Pair(
            ks.getCertificate("mesh") as X509Certificate,
            ks.getKey("mesh", "testpass".toCharArray()) as PrivateKey
        )
    }

    private suspend fun connectPair(
        server: Pair<X509Certificate, PrivateKey>,
        client: Pair<X509Certificate, PrivateKey>
    ): Pair<PairingSession, PairingSession> = coroutineScope {
        PairingListener(server, 0).use { listener ->
            val accepted = async { listener.accept() }
            val c = PairingSession.connect(client, "127.0.0.1", listener.port, null)
            Pair(accepted.await(), c)
        }
    }

    @Test
    fun `send delivers message to peer, onMessageReceived fires`() = runBlocking {
        val (serverPairing, clientPairing) = connectPair(idA, idB)
        val received = CompletableDeferred<MeshMessage>()
        val server = MeshSession.fromTransport(serverPairing.detachTransport(), "peer-client", "127.0.0.1", 0)
        val client = MeshSession.fromTransport(
            clientPairing.detachTransport(), "peer-server", "127.0.0.1", 0,
            onMessageReceived = { received.complete(it) }
        )
        try {
            server.send(ChatSend("msg-1", 1L, "hi from server"))

            val result = withTimeout(5000) { received.await() }
            assertTrue(result is ChatSend)
            assertEquals("hi from server", (result as ChatSend).text)
        } finally {
            server.close(); client.close()
        }
    }

    @Test
    fun `onStateChanged fires DISCONNECTED when peer closes`() = runBlocking {
        val (serverPairing, clientPairing) = connectPair(idA, idB)
        val states = mutableListOf<MeshSessionState>()
        val server = MeshSession.fromTransport(serverPairing.detachTransport(), "peer-client", "127.0.0.1", 0)
        val client = MeshSession.fromTransport(
            clientPairing.detachTransport(), "peer-server", "127.0.0.1", 0,
            onStateChanged = { states.add(it) }
        )
        try {
            server.close() // simulate peer going away

            delay(2000)
            assertTrue(states.contains(MeshSessionState.DISCONNECTED))
        } finally {
            client.close()
        }
    }

    /**
     * Subscribe-before-start regression test: a message is already written (and likely
     * buffered on the socket) BEFORE the receiving MeshSession is even constructed. Because
     * `fromTransport` accepts `onMessageReceived` as a parameter wired up before the read loop
     * is launched, this message must not be silently dropped.
     */
    @Test
    fun `message already buffered before session construction is still delivered`() = runBlocking {
        val (serverPairing, clientPairing) = connectPair(idA, idB)
        val serverTransport = serverPairing.detachTransport()
        val clientTransport = clientPairing.detachTransport()

        // Write directly on the raw transport, before any MeshSession exists on either side.
        MeshMessage.write(serverTransport.output, ChatSend("msg-2", 2L, "buffered before construction"))

        val received = CompletableDeferred<MeshMessage>()
        val server = MeshSession.fromTransport(serverTransport, "peer-client", "127.0.0.1", 0)
        val client = MeshSession.fromTransport(
            clientTransport, "peer-server", "127.0.0.1", 0,
            onMessageReceived = { received.complete(it) }
        )
        try {
            val result = withTimeout(5000) { received.await() }
            assertTrue(result is ChatSend)
            assertEquals("buffered before construction", (result as ChatSend).text)
        } finally {
            server.close(); client.close()
        }
    }

    @Test
    fun `drainPendingSends empties queue and returns ChatSend messages in order`() = runBlocking {
        val (serverPairing, clientPairing) = connectPair(idA, idB)
        val server = MeshSession.fromTransport(serverPairing.detachTransport(), "peer-client", "127.0.0.1", 0)
        val client = MeshSession.fromTransport(clientPairing.detachTransport(), "peer-server", "127.0.0.1", 0)
        try {
            // Disconnect the client by tearing down the peer, then queue sends while down.
            server.close()
            delay(2000) // allow the client's read/heartbeat loop to detect the disconnect

            client.send(ChatSend("msg-10", 10L, "first"))
            client.send(ChatSend("msg-20", 20L, "second"))

            val drained = client.drainPendingSends()

            assertEquals(2, drained.size)
            assertEquals("first", (drained[0] as ChatSend).text)
            assertEquals("second", (drained[1] as ChatSend).text)

            val drainedAgain = client.drainPendingSends()
            assertTrue(drainedAgain.isEmpty())
        } finally {
            client.close()
        }
    }

    /**
     * Finding I1 regression test: a stuck send (simulated here by holding [MeshSession.sendMutex]
     * for the whole test, as a large clip write stuck on a dead/slow peer would) must not prevent
     * the heartbeat loop's idle-timeout check from firing. Before the fix, the heartbeat loop
     * only ran its idle check right before trying to send a heartbeat through the SAME lock, so
     * a stuck send blocked the idle check indefinitely too. Uses short test-only intervals
     * (passed into `fromTransport`) instead of the real 20s/45s production values.
     */
    @Test
    fun `heartbeat loop idle-timeout fires even while send lock is held by a stuck send`() = runBlocking {
        val (serverPairing, clientPairing) = connectPair(idA, idB)
        val server = MeshSession.fromTransport(serverPairing.detachTransport(), "peer-client", "127.0.0.1", 0)
        val states = mutableListOf<MeshSessionState>()
        val client = MeshSession.fromTransport(
            clientPairing.detachTransport(), "peer-server", "127.0.0.1", 0,
            onStateChanged = { states.add(it) },
            heartbeatIntervalMs = 50L,
            idleTimeoutMs = 200L
        )
        try {
            // Simulate a send stuck mid-write (peer asleep, TCP buffer full): hold the lock for
            // the rest of the test and never release it. If the heartbeat loop still blocked on
            // this lock to run its idle check, DISCONNECTED would never fire below.
            client.sendMutex.lock()

            delay(2000)
            assertTrue(
                "expected DISCONNECTED to fire from the idle-timeout check despite the held send lock",
                states.contains(MeshSessionState.DISCONNECTED)
            )
        } finally {
            server.close(); client.close()
        }
    }
}
