package com.mesh.core

import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.fail
import org.junit.Test
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.X509Certificate

/**
 * The media plane over loopback, with the two committed test identities — the phone is the
 * sender in production, but both roles live in the same file and are exercised here.
 */
class CastChannelLoopbackTest {
    private val idA = loadFixture("identity_a.p12")
    private val idB = loadFixture("identity_b.p12")

    private fun loadFixture(name: String): Pair<X509Certificate, PrivateKey> {
        val ks = KeyStore.getInstance("PKCS12")
        javaClass.getResourceAsStream("/$name")!!.use { ks.load(it, "testpass".toCharArray()) }
        return Pair(
            ks.getCertificate("mesh") as X509Certificate,
            ks.getKey("mesh", "testpass".toCharArray()) as PrivateKey)
    }

    private fun sampleFrame() = CastFrame(CastFrameType.VIDEO_KEY, 42, byteArrayOf(9, 8, 7))

    @Test
    fun `frame from the expected peer with the right token arrives`() = runBlocking {
        val senderFingerprint = IdentityStore.fingerprint(idB.first)
        CastChannelListener.start(idA, "token-1", senderFingerprint).use { listener ->
            coroutineScope {
                val accepted = async { listener.accept() }
                val sender = CastChannel.connect(idB, "127.0.0.1", listener.port, "token-1")
                val receiver = withTimeout(10_000) { accepted.await() }

                sender.writeFrame(sampleFrame())
                val received = withTimeout(10_000) { receiver.readFrame() }

                assertEquals(CastFrameType.VIDEO_KEY, received.type)
                assertEquals(42L, received.timestampUs)
                assertArrayEquals(byteArrayOf(9, 8, 7), received.payload)
                sender.close()
                receiver.close()
            }
        }
    }

    @Test
    fun `wrong token is refused and the listener keeps listening`() = runBlocking {
        val senderFingerprint = IdentityStore.fingerprint(idB.first)
        CastChannelListener.start(idA, "token-1", senderFingerprint).use { listener ->
            // supervisorScope, not coroutineScope: the refused accept is *meant* to throw, and a
            // plain scope would cancel the whole test with it instead of letting it be asserted.
            supervisorScope {
                val refused = async { listener.accept() }
                val impostor = CastChannel.connect(idB, "127.0.0.1", listener.port, "token-2")
                try {
                    withTimeout(10_000) { refused.await() }
                    fail("expected the wrong token to be refused")
                } catch (e: Exception) {
                    // expected
                }
                impostor.close()

                val accepted = async { listener.accept() }
                val sender = CastChannel.connect(idB, "127.0.0.1", listener.port, "token-1")
                val receiver = withTimeout(10_000) { accepted.await() }
                sender.writeFrame(sampleFrame())

                assertEquals(CastFrameType.VIDEO_KEY, withTimeout(10_000) { receiver.readFrame() }.type)
                sender.close()
                receiver.close()
            }
        }
    }

    @Test
    fun `another device's certificate is refused`() = runBlocking {
        // The listener expects idA as the sender; idB turns up instead.
        val wrongFingerprint = IdentityStore.fingerprint(idA.first)
        CastChannelListener.start(idA, "token-1", wrongFingerprint).use { listener ->
            supervisorScope {
                val accepted = async { listener.accept() }
                val impostor = CastChannel.connect(idB, "127.0.0.1", listener.port, "token-1")
                try {
                    withTimeout(10_000) { accepted.await() }
                    fail("expected a foreign certificate to be refused")
                } catch (e: Exception) {
                    // expected
                }
                impostor.close()
            }
        }
    }
}
