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
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLSocket

enum class MeshSessionState { CONNECTED, DISCONNECTED, RECONNECTING }

/**
 * A persistent, reconnecting channel to a paired peer, carrying framed [MeshMessage] traffic
 * after the pairing handshake completes. Owns the transport from the point it is constructed
 * (via [fromTransport]) until [close].
 *
 * The reconnect loop here is intentionally minimal: on disconnect it only probes that the
 * peer's address is reachable again, then stops, handing control back to the caller for the
 * actual trusted-reconnect handshake (re-running PairingSession.connect + isPeerTrusted +
 * detachTransport and replacing this MeshSession instance) — mirroring the Windows design.
 */
class MeshSession private constructor(
    private var socket: SSLSocket?,
    private var input: DataInputStream?,
    private var output: DataOutputStream?,
    private val peerUuid: String,
    private var peerIp: String,
    private val peerPort: Int
) {
    companion object {
        private const val HEARTBEAT_INTERVAL_MS = 20_000L
        private const val IDLE_TIMEOUT_MS = 45_000L
        private val BACKOFF_STEPS_MS = longArrayOf(1_000, 2_000, 4_000, 8_000)
        private const val MAX_BACKOFF_MS = 30_000L

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
            onStateChanged: ((MeshSessionState) -> Unit)? = null
        ): MeshSession {
            val session = MeshSession(transport.socket, transport.input, transport.output, peerUuid, peerIp, peerPort)
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
    private val sendMutex = Mutex()
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
     * failed send) and returns its [ClipboardPush] contents in order, discarding any queued
     * [Heartbeat] entries. The caller (reconnect orchestration) is responsible for re-sending
     * the drained messages through a replacement session.
     */
    suspend fun drainPendingSends(): List<MeshMessage> = sendMutex.withLock {
        val drained = pendingSend.filterIsInstance<ClipboardPush>()
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
            } catch (e: SocketTimeoutException) {
                // No data within the socket's SO_TIMEOUT: not a failure, idle timeout is
                // tracked separately via lastReceivedAt in runHeartbeatLoop.
                continue
            } catch (e: Exception) {
                if (!job.isActive) return
                markDisconnected()
                return
            }
        }
    }

    private suspend fun runHeartbeatLoop() {
        while (job.isActive) {
            delay(HEARTBEAT_INTERVAL_MS)
            if (!job.isActive) return
            if (stateRef.get() == MeshSessionState.CONNECTED) {
                if (System.currentTimeMillis() - lastReceivedAt > IDLE_TIMEOUT_MS) {
                    markDisconnected()
                    continue
                }
                try {
                    send(Heartbeat)
                } catch (e: Exception) {
                    // send() already records the failure and transitions state.
                }
            }
        }
    }

    private suspend fun reconnectLoop() {
        transitionTo(MeshSessionState.RECONNECTING)
        var attempt = 0
        while (job.isActive) {
            attempt++
            try {
                val probe = Socket()
                probe.connect(InetSocketAddress(peerIp, peerPort), 3_000)
                probe.close()
                // Signal: the peer's address is reachable again. The caller (reconnect
                // orchestration layer) must now re-run the trusted-peer TLS handshake and
                // replace this MeshSession instance; this loop's job is done.
                return
            } catch (e: Exception) {
                val delayMs = if (attempt <= BACKOFF_STEPS_MS.size) BACKOFF_STEPS_MS[attempt - 1] else MAX_BACKOFF_MS
                delay(delayMs)
            }
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
            scope.launch { reconnectLoop() }
        }
    }

    fun close() {
        job.cancel()
        try { socket?.close() } catch (e: Exception) { /* already closing */ }
    }
}
