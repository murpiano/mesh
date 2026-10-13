package com.mesh.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Socket
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.X509Certificate
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

/**
 * Timeout budget notes (see plan Global Constraint: "TLS handshake timeout: 10 seconds").
 *
 * The brief's reference implementation set SO_TIMEOUT = 10_000 only on the *listening*
 * ServerSocket (bounding how long `accept()` may block) and, on the client side, relied on
 * `SSLSocketFactory.createSocket(host, port)` which performs the raw TCP connect with NO
 * timeout at all before SO_TIMEOUT is ever applied. In both cases the actual TLS handshake
 * (`startHandshake()`) that follows was left unbounded on the accepted/connected socket:
 *
 *   - Server: `serverSocket.soTimeout` only governs `serverSocket.accept()`. The SSLSocket
 *     returned by `accept()` is a *different* socket object with its own (default = infinite)
 *     SO_TIMEOUT. Calling `socket.startHandshake()` on it without first setting its own
 *     `soTimeout` means a stalled/hostile peer can hang the handshake forever.
 *   - Client: `factory.createSocket(ip, port)` resolves + connects + wraps in one blocking
 *     call with no timeout parameter, so the TCP connect phase itself is unbounded. Only
 *     after that call returns does the brief's code set `soTimeout`, which then only protects
 *     the handshake, not the connect.
 *
 * This is the same category of bug the Windows PairingSession.cs implementation had (Task 5):
 * there, the TLS handshake wasn't wrapped in the 10s timeout, only the TCP accept/connect was.
 * Here it's the mirror image risk on one side (server: handshake unbounded) plus a connect-side
 * gap that plain SO_TIMEOUT can't fix (you cannot set a "connect timeout" after the fact).
 *
 * Fix applied below:
 *   - Server: explicitly set `socket.soTimeout = 10_000` on the *accepted* SSLSocket before
 *     calling `startHandshake()`.
 *   - Client: connect a plain `Socket` with an explicit connect timeout
 *     (`socket.connect(address, 10_000)`), THEN layer TLS on top via
 *     `sslContext.socketFactory.createSocket(rawSocket, host, port, true)`, THEN set
 *     `soTimeout = 10_000` on the result before calling `startHandshake()`.
 *
 * Verification of the underlying assumption — does `SSLSocket.startHandshake()` actually
 * respect `Socket.setSoTimeout()`? Per the documented contract of `SSLSocket`/`SSLEngine`-backed
 * blocking sockets in the JDK/Android implementation, the handshake is driven by ordinary
 * blocking reads/writes on the underlying socket's streams. `SO_TIMEOUT` governs the blocking
 * read calls (`Socket.getInputStream().read()`), and the handshake's internal reads go through
 * that same input stream, so a stalled peer during handshake causes `startHandshake()` to throw
 * `SSLException` (wrapping a `SocketTimeoutException`) once SO_TIMEOUT elapses — the same way a
 * stalled application-data `read()` would. This is well-established, documented behavior (not
 * something that only applies to plain TCP reads), so setting `soTimeout` before
 * `startHandshake()` is sufficient; no extra `withTimeout {}` coroutine wrapper is required to
 * make the handshake itself bounded. (A coroutine-level `withTimeout` around a blocking
 * `Dispatchers.IO` call would not reliably interrupt the underlying blocking socket read anyway
 * — cancellation of a blocking call requires closing the socket, which `SO_TIMEOUT` already
 * achieves more precisely by scoping the bound to this exact operation.)
 */
class PairingSession {
    var peerFingerprint: String? = null
    private var sslSocket: SSLSocket? = null

    private companion object {
        const val TIMEOUT_MS = 10_000
    }

    private fun buildSslContext(identity: Pair<X509Certificate, PrivateKey>): SSLContext {
        val keyStore = KeyStore.getInstance("PKCS12")
        keyStore.load(null, null)
        keyStore.setKeyEntry("mesh", identity.second, charArrayOf(), arrayOf(identity.first))

        val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
        kmf.init(keyStore, charArrayOf())

        // Untrusted at handshake stage, per spec — trust is established via the PIN step afterward.
        val trustAllManager = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) {}
            override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {}
            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
        }

        val context = SSLContext.getInstance("TLS")
        context.init(kmf.keyManagers, arrayOf<TrustManager>(trustAllManager), null)
        return context
    }

    suspend fun startListener(identity: Pair<X509Certificate, PrivateKey>, port: Int) {
        withContext(Dispatchers.IO) {
            val context = buildSslContext(identity)
            val factory = context.serverSocketFactory
            val serverSocket = factory.createServerSocket(port) as SSLServerSocket
            serverSocket.needClientAuth = true
            // Bounds how long we wait for an incoming connection.
            serverSocket.soTimeout = TIMEOUT_MS

            val socket = serverSocket.accept() as SSLSocket
            // The accepted socket is a distinct object with its own (otherwise infinite)
            // SO_TIMEOUT — must be set explicitly so the handshake itself is bounded too.
            socket.soTimeout = TIMEOUT_MS
            socket.startHandshake()
            serverSocket.close()

            sslSocket = socket
            val peerCert = socket.session.peerCertificates[0] as X509Certificate
            peerFingerprint = IdentityStore.fingerprint(peerCert)
        }
    }

    suspend fun connectTo(identity: Pair<X509Certificate, PrivateKey>, ip: String, port: Int) {
        withContext(Dispatchers.IO) {
            val context = buildSslContext(identity)

            // Connect the raw TCP socket with an explicit timeout first — the factory's
            // combined create+connect call has no timeout parameter and could otherwise
            // block for the platform's (potentially much longer) default.
            val rawSocket = Socket()
            rawSocket.connect(InetSocketAddress(ip, port), TIMEOUT_MS)

            // Layer TLS on top of the already-connected socket.
            val socket = context.socketFactory.createSocket(rawSocket, ip, port, true) as SSLSocket
            socket.soTimeout = TIMEOUT_MS
            socket.startHandshake()

            sslSocket = socket
            val peerCert = socket.session.peerCertificates[0] as X509Certificate
            peerFingerprint = IdentityStore.fingerprint(peerCert)
        }
    }

    fun computePin(localCert: X509Certificate): String {
        val fp = peerFingerprint ?: throw IllegalStateException("Handshake has not completed yet.")
        return PinDerivation.derive(IdentityStore.fingerprint(localCert), fp)
    }

    suspend fun isPeerTrusted(dao: PairedDeviceDao, uuid: String): Boolean {
        val record = dao.findByUuid(uuid) ?: return false
        return record.fingerprint == peerFingerprint
    }
}
