package com.mesh.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import java.io.Closeable
import java.security.PrivateKey
import java.security.cert.X509Certificate
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket

/**
 * Long-lived TLS listener for incoming pairing connections. Binds once (port 0 = OS picks a
 * free ephemeral port; advertise [port] via mDNS) and stays bound until [close]. Each accepted
 * connection is handshaken, handled and closed independently, so a failed/timed-out attempt
 * never leaks or blocks the listening port.
 */
class PairingListener(
    private val identity: Pair<X509Certificate, PrivateKey>,
    port: Int = 0
) : Closeable {
    private val serverSocket: SSLServerSocket =
        (PairingSession.buildSslContext(identity).serverSocketFactory.createServerSocket(port) as SSLServerSocket)
            .apply { needClientAuth = true }

    val port: Int get() = serverSocket.localPort

    /**
     * Waits (until [close]) for one incoming connection and performs the TLS handshake
     * (bounded by 10 s). The returned session is owned by the caller. If the handshake
     * fails, the accepted socket is closed before rethrowing.
     */
    suspend fun accept(): PairingSession = withContext(Dispatchers.IO) {
        val socket = serverSocket.accept() as SSLSocket
        PairingSession.handshakeAccepted(socket, identity.first)
    }

    /**
     * Accept loop: handles one incoming connection at a time via [handler] (on the caller's
     * dispatcher) and then goes back to listening, until [close] or cancellation. Handshake
     * failures are reported through [onError] and do not stop the loop. Each session is closed
     * after its handler returns.
     */
    suspend fun runAcceptLoop(
        handler: suspend (PairingSession) -> Unit,
        onError: (Exception) -> Unit = {}
    ) {
        while (currentCoroutineContext().isActive && !serverSocket.isClosed) {
            val session = try {
                accept()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (serverSocket.isClosed) return
                onError(e) // e.g. handshake timeout from a stalled peer
                continue
            }
            session.use {
                try {
                    handler(it)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    onError(e)
                }
            }
        }
    }

    override fun close() {
        runCatching { serverSocket.close() }
    }
}
