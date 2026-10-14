package com.mesh.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.selects.select
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.Closeable
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

enum class PeerTrust {
    /** No stored record for this uuid: first-time pairing. */
    UNKNOWN,
    /** Stored record exists and its fingerprint matches the live peer certificate. */
    TRUSTED,
    /** Stored record exists but the live fingerprint differs: warn, then force re-pair. */
    CERTIFICATE_CHANGED
}

enum class PairingResult {
    /** First-time (or re-) pairing completed on both sides; record written. */
    PAIRED,
    /** Both sides already trusted each other; no PIN needed. */
    RECONNECTED,
    /** TCP/TLS/protocol-transport failure, including timeouts. UI: "Connection failed". */
    CONNECTION_FAILED,
    /** Connected, but PIN rejected/declined/timed out or nonce check failed. UI: "Pairing failed". */
    PAIRING_FAILED
}

data class PairingOutcome(
    val result: PairingResult,
    val peerUuid: String?,
    val peerName: String?,
    val detail: String
)

/**
 * UI callbacks used by [PairingSession.run]. Both are invoked on the caller's dispatcher
 * (Main in the app). The coroutine is cancelled when the user-interaction budget expires or
 * the peer aborts; implementations must dismiss their dialog on cancellation.
 */
interface PairingPrompt {
    /** "Certificate changed for <name> — re-pair?". Return true to continue into the PIN flow. */
    suspend fun confirmCertificateChanged(peerName: String): Boolean

    /** Show the PIN; return true for "Match", false for "Not matching". */
    suspend fun confirmPin(peerName: String, pin: String): Boolean
}

/** Thrown when the peer's revealed nonce does not match its earlier commitment. */
class PairingFailedException(message: String) : Exception(message)

/** The open, authenticated transport handed off by [PairingSession.detachTransport]. */
data class SessionTransport(
    val socket: SSLSocket,
    val input: DataInputStream,
    val output: DataOutputStream
)

/**
 * One open TLS connection to a peer plus the post-handshake pairing protocol
 * ([PairingProtocol]). The socket stays open after the handshake so the protocol can run over
 * it; [close] it in every exit path (the app and tests use `use {}`).
 *
 * Timeouts: every blocking read is bounded by the socket's SO_TIMEOUT — 10 s for the TCP
 * connect, TLS handshake (see note below) and protocol steps 1-5; the confirm step (6) uses a
 * separate user-interaction budget ([userTimeoutMs], 60 s by default).
 *
 * Note on the handshake timeout: `SSLSocket.startHandshake()` performs ordinary blocking
 * reads on the underlying socket, so SO_TIMEOUT bounds it (a stalled peer makes it throw a
 * SocketTimeoutException). The *accepted* socket of a ServerSocket is a distinct object whose
 * SO_TIMEOUT must be set explicitly; and the client must connect the raw TCP socket with an
 * explicit connect timeout before layering TLS on top, because
 * `SSLSocketFactory.createSocket(host, port)` has no connect-timeout parameter.
 */
class PairingSession private constructor(
    private val socket: SSLSocket,
    private val localCertificate: X509Certificate
) : Closeable {
    val peerFingerprint: String =
        IdentityStore.fingerprint(socket.session.peerCertificates[0] as X509Certificate)

    // IPv4-mapped IPv6 addresses are already surfaced as Inet4Address by the JDK/Android.
    val peerIp: String = socket.inetAddress?.hostAddress ?: ""

    var peerUuid: String? = null
        private set
    var peerName: String? = null
        private set

    /** Budget for the local prompt, and separately for awaiting the peer's confirm byte. */
    var userTimeoutMs: Long = DEFAULT_USER_TIMEOUT_MS

    private val input = DataInputStream(BufferedInputStream(socket.inputStream))
    private val output = DataOutputStream(BufferedOutputStream(socket.outputStream))
    private var localNonce: ByteArray? = null
    private var peerNonce: ByteArray? = null

    companion object {
        const val NETWORK_TIMEOUT_MS = 10_000
        const val DEFAULT_USER_TIMEOUT_MS = 60_000L
        private const val CLOSE_TIMEOUT_MS = 1_000

        private val closer = Executors.newCachedThreadPool { r ->
            Thread(r, "mesh-session-close").apply { isDaemon = true }
        }

        /** Close without letting a TLS 1.3 close_notify wait hold the caller for SO_TIMEOUT. */
        private fun closeQuickly(socket: Socket) {
            runCatching { socket.soTimeout = CLOSE_TIMEOUT_MS }
            runCatching { socket.close() }
        }

        fun buildSslContext(identity: Pair<X509Certificate, PrivateKey>): SSLContext {
            // Untrusted at handshake stage, per spec — trust is established via the PIN step
            // (or the pinned fingerprint on reconnect) afterwards.
            val trustAllManager = object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) {}
                override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {}
                override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
            }
            val context = SSLContext.getInstance("TLS")
            context.init(
                arrayOf(SingleKeyManager(identity.first, identity.second)),
                arrayOf<TrustManager>(trustAllManager),
                null)
            return context
        }

        /**
         * Initiator side: TCP connect + TLS handshake, each bounded by 10 s.
         * [tlsProtocols] optionally restricts the offered TLS versions (used by tests to cover
         * both TLS 1.2 and TLS 1.3 with Keystore-backed keys).
         */
        suspend fun connect(
            identity: Pair<X509Certificate, PrivateKey>,
            ip: String,
            port: Int,
            tlsProtocols: Array<String>? = null
        ): PairingSession = withContext(Dispatchers.IO) {
            val rawSocket = Socket()
            try {
                rawSocket.connect(InetSocketAddress(ip, port), NETWORK_TIMEOUT_MS)
                val socket = buildSslContext(identity).socketFactory
                    .createSocket(rawSocket, ip, port, true) as SSLSocket
                try {
                    if (tlsProtocols != null) socket.enabledProtocols = tlsProtocols
                    socket.soTimeout = NETWORK_TIMEOUT_MS
                    socket.startHandshake()
                    PairingSession(socket, identity.first)
                } catch (e: Exception) {
                    closeQuickly(socket)
                    throw e
                }
            } catch (e: Exception) {
                closeQuickly(rawSocket)
                throw e
            }
        }

        /** Listener side: bounded TLS handshake on an accepted socket; closes it on failure. */
        internal fun handshakeAccepted(socket: SSLSocket, localCertificate: X509Certificate): PairingSession {
            try {
                socket.soTimeout = NETWORK_TIMEOUT_MS
                socket.startHandshake()
                return PairingSession(socket, localCertificate)
            } catch (e: Exception) {
                closeQuickly(socket)
                throw e
            }
        }

        suspend fun checkTrust(dao: PairedDeviceDao, uuid: String, fingerprint: String): PeerTrust {
            val record = dao.findByUuid(uuid) ?: return PeerTrust.UNKNOWN
            return if (record.fingerprint == fingerprint) PeerTrust.TRUSTED else PeerTrust.CERTIFICATE_CHANGED
        }
    }

    /** Protocol steps 1-2: send own uuid/name, receive the peer's. */
    suspend fun exchangeIdentity(selfUuid: String, selfName: String) = withContext(Dispatchers.IO) {
        socket.soTimeout = NETWORK_TIMEOUT_MS
        PairingProtocol.writeString(output, selfUuid)
        PairingProtocol.writeString(output, selfName)
        peerUuid = PairingProtocol.readString(input)
        peerName = PairingProtocol.readString(input)
    }

    /** Protocol step 3. Returns true only if BOTH sides trust each other. */
    suspend fun exchangeTrust(localTrustsPeer: Boolean): Boolean = withContext(Dispatchers.IO) {
        socket.soTimeout = NETWORK_TIMEOUT_MS
        PairingProtocol.writeByte(output, if (localTrustsPeer) PairingProtocol.YES else PairingProtocol.NO)
        val peer = PairingProtocol.readByte(input)
        localTrustsPeer && peer == PairingProtocol.YES
    }

    /**
     * Protocol steps 4-5: commit-then-reveal nonce exchange.
     * @throws PairingFailedException if the peer's nonce does not match its commitment.
     */
    suspend fun exchangeNonces() = withContext(Dispatchers.IO) {
        socket.soTimeout = NETWORK_TIMEOUT_MS
        val nonce = PairingProtocol.createNonce()
        PairingProtocol.writeBytes(output, PairingProtocol.commit(nonce))
        val peerCommitment = PairingProtocol.readBytes(input, PairingProtocol.COMMITMENT_LENGTH)
        // Reveal only after the peer has committed.
        PairingProtocol.writeBytes(output, nonce)
        val revealed = PairingProtocol.readBytes(input, PinDerivation.NONCE_LENGTH)
        if (!PairingProtocol.verifyCommitment(peerCommitment, revealed)) {
            throw PairingFailedException("Peer's nonce does not match its commitment.")
        }
        localNonce = nonce
        peerNonce = revealed
    }

    fun computePin(): String {
        val local = localNonce ?: throw IllegalStateException("Nonce exchange has not completed yet.")
        val peer = peerNonce ?: throw IllegalStateException("Nonce exchange has not completed yet.")
        return PinDerivation.derive(IdentityStore.fingerprint(localCertificate), peerFingerprint, local, peer)
    }

    suspend fun isPeerTrusted(dao: PairedDeviceDao, uuid: String): Boolean =
        checkTrust(dao, uuid, peerFingerprint) == PeerTrust.TRUSTED

    /**
     * Runs the whole post-handshake protocol and writes the trust record only on mutual
     * success. Identical on the initiator and listener side. Network/peer errors are reported
     * through the returned outcome rather than thrown. Does not close the session on success;
     * callers own it (`use {}`). On failure the socket is closed.
     */
    suspend fun run(selfUuid: String, selfName: String, dao: PairedDeviceDao, prompt: PairingPrompt): PairingOutcome {
        val trust: PeerTrust
        val pin: String
        try {
            exchangeIdentity(selfUuid, selfName)
            val uuid = peerUuid!!
            trust = checkTrust(dao, uuid, peerFingerprint)
            if (exchangeTrust(trust == PeerTrust.TRUSTED)) {
                val existing = dao.findByUuid(uuid)!!
                dao.upsert(existing.copy(name = peerName!!, lastIp = peerIp))
                return outcome(PairingResult.RECONNECTED, "Trusted reconnect.")
            }
            exchangeNonces()
            pin = computePin()
        } catch (e: CancellationException) {
            close()
            throw e
        } catch (e: PairingFailedException) {
            close()
            return outcome(PairingResult.PAIRING_FAILED, e.message ?: "Pairing failed.")
        } catch (e: Exception) {
            close()
            return outcome(PairingResult.CONNECTION_FAILED, e.message ?: e.javaClass.simpleName)
        }

        // Step 6: user confirmation, raced against the peer's answer so an early
        // "Not matching" on the other device dismisses our dialog immediately.
        val confirmed = try {
            confirmWithPeer(trust, pin, prompt)
        } catch (e: CancellationException) {
            close()
            throw e
        } catch (e: Exception) {
            false
        }
        if (!confirmed) {
            close()
            return outcome(PairingResult.PAIRING_FAILED, "PIN not confirmed on both devices.")
        }

        dao.upsert(PairedDevice(
            uuid = peerUuid!!,
            name = peerName!!,
            fingerprint = peerFingerprint,
            lastIp = peerIp,
            pairedAt = System.currentTimeMillis()
        ))
        return outcome(PairingResult.PAIRED, "Paired.")
    }

    private suspend fun confirmWithPeer(trust: PeerTrust, pin: String, prompt: PairingPrompt): Boolean =
        coroutineScope {
            // Covers the local prompt plus the wait for the peer; the explicit timeouts below
            // are the real bounds, this only guarantees the blocking read cannot outlive them.
            socket.soTimeout = (2 * userTimeoutMs + NETWORK_TIMEOUT_MS).toInt()
            val peerAnswer = async(Dispatchers.IO) { runCatching { PairingProtocol.readByte(input) } }
            val localAnswer = async {
                withTimeoutOrNull(userTimeoutMs) { askUser(trust, pin, prompt) } ?: false
            }

            val peerFirst = select {
                peerAnswer.onAwait { true }
                localAnswer.onAwait { false }
            }
            if (peerFirst && peerAnswer.await().getOrNull() != PairingProtocol.YES) {
                localAnswer.cancel() // dismiss our dialog
                runCatching { withContext(Dispatchers.IO) { PairingProtocol.writeByte(output, PairingProtocol.NO) } }
                close()
                return@coroutineScope false
            }

            val localMatch = localAnswer.await()
            val sent = runCatching {
                withContext(Dispatchers.IO) {
                    PairingProtocol.writeByte(output, if (localMatch) PairingProtocol.YES else PairingProtocol.NO)
                }
            }.isSuccess
            if (!localMatch || !sent) {
                close() // unblocks the pending peer read so this scope can complete
                return@coroutineScope false
            }

            val peer = withTimeoutOrNull(userTimeoutMs) { peerAnswer.await() }
            if (peer == null) close() // timed out: closing unblocks the read
            peer?.getOrNull() == PairingProtocol.YES
        }

    private suspend fun askUser(trust: PeerTrust, pin: String, prompt: PairingPrompt): Boolean = try {
        val name = peerName ?: "Unknown device"
        if (trust == PeerTrust.CERTIFICATE_CHANGED && !prompt.confirmCertificateChanged(name)) {
            false
        } else {
            prompt.confirmPin(name, pin)
        }
    } catch (e: CancellationException) {
        throw e
    } catch (e: Exception) {
        false // UI error (e.g. Activity finishing): treat as "Not matching"
    }

    private fun outcome(result: PairingResult, detail: String) = PairingOutcome(result, peerUuid, peerName, detail)

    private val closed = AtomicBoolean(false)

    /**
     * Hands ownership of the open, authenticated transport to the caller (e.g. to build a
     * [MeshSession] on a Paired/Reconnected outcome). After this call, this session's own
     * [close] becomes a no-op — the caller now owns closing the transport.
     */
    fun detachTransport(): SessionTransport {
        closed.set(true)
        return SessionTransport(socket, input, output)
    }

    /**
     * Idempotent, non-blocking close, safe to call from Main (e.g. via `use {}`).
     *
     * A graceful TLS close writes a close_notify alert (network I/O — not allowed on Main
     * under StrictMode), and the JDK's TLS 1.3 implementation additionally waits for the
     * peer's close_notify for up to SO_TIMEOUT (which is 130 s during the confirm step). So
     * the actual close runs on a background thread with a short SO_TIMEOUT.
     */
    override fun close() {
        if (closed.getAndSet(true)) return
        closer.execute { closeQuickly(socket) }
    }
}
