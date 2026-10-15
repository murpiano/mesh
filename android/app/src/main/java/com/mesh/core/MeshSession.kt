package com.mesh.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.DataInputStream
import java.io.DataOutputStream
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLSocket

enum class MeshSessionState { CONNECTED, DISCONNECTED, RECONNECTING }

/**
 * A channel to a paired peer, carrying framed [MeshMessage] traffic after the pairing handshake
 * completes. Owns the transport from the point it is constructed (via [fromTransport]) until
 * [close].
 *
 * This class only detects that the transport died (read/write failure, or no traffic within the
 * idle timeout) and reports it via [onStateChanged] == DISCONNECTED; it does not reconnect
 * itself. The owner (MainActivity) listens for that event and re-runs PairingSession.connect +
 * isPeerTrusted + detachTransport on success, constructing a fresh [MeshSession] via
 * [fromTransport] to replace this one — the single reconnect loop and policy (dialer/passive
 * tie-break, backoff) live there.
 */
class MeshSession private constructor(
    private var socket: SSLSocket?,
    private var input: DataInputStream?,
    private var output: DataOutputStream?,
    private val peerUuid: String,
    private var peerIp: String,
    private val peerPort: Int,
    // Test-only seam: production call sites always take the companion defaults below. Letting
    // tests pass short durations lets them exercise the real heartbeat/idle-timeout loop without
    // waiting out the real 20s/45s production intervals.
    private val heartbeatIntervalMs: Long = HEARTBEAT_INTERVAL_MS,
    private val idleTimeoutMs: Long = IDLE_TIMEOUT_MS
) {
    companion object {
        private const val HEARTBEAT_INTERVAL_MS = 20_000L
        private const val IDLE_TIMEOUT_MS = 45_000L

        /**
         * Builds a session from an already-open, authenticated transport and starts its
         * read/heartbeat loops.
         *
         * [onMessageReceived] and [onStateChanged] are accepted as parameters (rather than
         * only exposed as mutable properties set after this returns) so they are wired up
         * BEFORE the read/heartbeat coroutines are launched. Otherwise a message already
         * buffered on the socket could be read and dispatched before the caller has a chance
         * to assign the properties, and be silently dropped.
         */
        fun fromTransport(
            transport: SessionTransport,
            peerUuid: String,
            peerIp: String,
            peerPort: Int,
            onMessageReceived: ((MeshMessage) -> Unit)? = null,
            onStateChanged: ((MeshSessionState) -> Unit)? = null,
            heartbeatIntervalMs: Long = HEARTBEAT_INTERVAL_MS,
            idleTimeoutMs: Long = IDLE_TIMEOUT_MS
        ): MeshSession {
            // The pairing-phase SO_TIMEOUT (10s, or up to 130s during the PIN-confirm step) only
            // makes sense while that handshake is running. On the persistent channel, a blocked
            // read must wait indefinitely rather than throw: dead-connection detection is handled
            // independently and robustly by the heartbeat/idle-timeout mechanism below, which
            // closes the socket on timeout and so unblocks any pending read with an IOException.
            runCatching { transport.socket.soTimeout = 0 }
            val session = MeshSession(transport.socket, transport.input, transport.output, peerUuid, peerIp,
                peerPort, heartbeatIntervalMs, idleTimeoutMs)
            session.onMessageReceived = onMessageReceived
            session.onStateChanged = onStateChanged
            session.scope.launch { session.runReadLoop() }
            session.scope.launch { session.runHeartbeatLoop() }
            return session
        }
    }

    var onMessageReceived: ((MeshMessage) -> Unit)? = null
    var onStateChanged: ((MeshSessionState) -> Unit)? = null

    private val job = Job()
    private val scope = CoroutineScope(Dispatchers.IO + job)
    // Internal (not private) so tests in this module can simulate a send stuck mid-write by
    // holding this lock directly, to prove the heartbeat loop's idle-timeout check still fires
    // without blocking on it (Finding I1).
    internal val sendMutex = Mutex()
    private val pendingSend = mutableListOf<MeshMessage>()

    // Atomic state: the transition function below does an exchange-and-compare so that only
    // the caller who actually changes the value fires onStateChanged — avoiding duplicate
    // events when two concurrent failure paths (read loop + heartbeat loop) race to transition
    // to DISCONNECTED at nearly the same time.
    private val stateRef = AtomicReference(MeshSessionState.CONNECTED)

    @Volatile private var lastReceivedAt = System.currentTimeMillis()

    val state: MeshSessionState get() = stateRef.get()

    suspend fun send(message: MeshMessage) {
        sendMutex.withLock {
            val out = output
            if (stateRef.get() != MeshSessionState.CONNECTED || out == null) {
                pendingSend.add(message)
                return
            }
            try {
                MeshMessage.write(out, message)
            } catch (e: Exception) {
                pendingSend.add(message)
                markDisconnected()
            }
        }
    }

    /**
     * Empties the pending-send queue (messages queued by [send] while disconnected, or after a
     * failed send) and returns everything except queued [Heartbeat] entries, in order — a
     * heartbeat is only meaningful on the transport it was generated for. The caller (reconnect
     * orchestration) is responsible for re-sending the drained messages through a replacement
     * session.
     */
    suspend fun drainPendingSends(): List<MeshMessage> = sendMutex.withLock {
        val drained = pendingSend.filter { it !is Heartbeat }
        pendingSend.clear()
        drained
    }

    private suspend fun runReadLoop() {
        while (job.isActive) {
            val currentInput = input
            if (currentInput == null) {
                delay(200)
                continue
            }
            try {
                val message = MeshMessage.read(currentInput)
                lastReceivedAt = System.currentTimeMillis()
                if (message !is Heartbeat) onMessageReceived?.invoke(message)
            } catch (e: Exception) {
                if (!job.isActive) return
                markDisconnected()
                return
            }
        }
    }

    private suspend fun runHeartbeatLoop() {
        while (job.isActive) {
            delay(heartbeatIntervalMs)
            if (!job.isActive) return
            if (stateRef.get() != MeshSessionState.CONNECTED) continue

            // The idle-timeout check must run regardless of whether the send lock can be
            // acquired below: a heartbeat send stuck behind an in-flight large-clip write
            // (peer asleep, TCP send buffer full) must never prevent us from noticing a dead
            // connection. Do NOT gate this on a successful lock acquisition.
            if (System.currentTimeMillis() - lastReceivedAt > idleTimeoutMs) {
                markDisconnected()
                continue
            }

            trySendHeartbeat()
        }
    }

    /**
     * Non-blocking heartbeat send: if [sendMutex] is already held by an in-flight [send] (e.g.
     * a large clip currently being written), skip sending the heartbeat this cycle rather than
     * block waiting for the lock — the idle-timeout check in [runHeartbeatLoop] runs
     * independently of this and is what actually detects a dead connection.
     */
    private suspend fun trySendHeartbeat() {
        if (!sendMutex.tryLock()) return
        try {
            val out = output
            if (stateRef.get() != MeshSessionState.CONNECTED || out == null) return
            try {
                MeshMessage.write(out, Heartbeat)
            } catch (e: Exception) {
                markDisconnected()
            }
        } finally {
            sendMutex.unlock()
        }
    }

    /** Atomic exchange-and-compare: fires [onStateChanged] only if the state actually changed. */
    private fun transitionTo(newState: MeshSessionState): Boolean {
        val previous = stateRef.getAndSet(newState)
        if (previous == newState) return false
        onStateChanged?.invoke(newState)
        return true
    }

    private fun markDisconnected() {
        if (transitionTo(MeshSessionState.DISCONNECTED)) {
            try { socket?.close() } catch (e: Exception) { /* already closing */ }
        }
    }

    fun close() {
        job.cancel()
        try { socket?.close() } catch (e: Exception) { /* already closing */ }
    }
}
