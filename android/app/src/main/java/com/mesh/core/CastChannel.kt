package com.mesh.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.Closeable
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.Base64
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket

/**
 * The media plane of one cast: a TLS connection that carries nothing but [CastFrame]s, opened
 * for the cast and closed with it. Separate from the session on purpose — a picture must never
 * delay a heartbeat or a chat message.
 *
 * It is not a second way into a device. The listener accepts a connection only when the peer
 * certificate's fingerprint is the one the trust store already holds for that device AND the
 * dialer presents the one-time token the receiver handed out over the (already authenticated)
 * session. MUST stay wire-compatible with src/windows/Mesh.Core/CastChannel.cs.
 */
class CastChannel private constructor(private val socket: SSLSocket) : Closeable {
    private val input = DataInputStream(BufferedInputStream(socket.inputStream))
    private val output = DataOutputStream(BufferedOutputStream(socket.outputStream))

    suspend fun writeFrame(frame: CastFrame) = withContext(Dispatchers.IO) {
        CastFrame.write(output, frame)
    }

    suspend fun readFrame(): CastFrame = withContext(Dispatchers.IO) { CastFrame.read(input) }

    override fun close() {
        runCatching { socket.close() }
    }

    companion object {
        private const val HANDSHAKE_TIMEOUT_MS = 10_000

        /** Sending side: dials the receiver's media listener and presents the token. */
        suspend fun connect(
            identity: Pair<X509Certificate, PrivateKey>,
            ip: String,
            port: Int,
            token: String
        ): CastChannel = withContext(Dispatchers.IO) {
            val rawSocket = Socket()
            try {
                rawSocket.connect(InetSocketAddress(ip, port), HANDSHAKE_TIMEOUT_MS)
                val socket = PairingSession.buildSslContext(identity).socketFactory
                    .createSocket(rawSocket, ip, port, true) as SSLSocket
                try {
                    socket.soTimeout = HANDSHAKE_TIMEOUT_MS
                    socket.startHandshake()
                    val channel = CastChannel(socket)
                    PairingProtocol.writeString(channel.output, token)
                    channel.output.flush()
                    // Frames can take as long as they take once the cast is running; the cast's
                    // own stop path is what ends a channel that went quiet.
                    socket.soTimeout = 0
                    channel
                } catch (e: Exception) {
                    runCatching { socket.close() }
                    throw e
                }
            } catch (e: Exception) {
                runCatching { rawSocket.close() }
                throw e
            }
        }

        internal fun accept(
            socket: SSLSocket,
            expectedToken: String,
            expectedFingerprint: String
        ): CastChannel {
            try {
                socket.soTimeout = HANDSHAKE_TIMEOUT_MS
                socket.startHandshake()
                val peerCert = socket.session.peerCertificates[0] as X509Certificate
                if (IdentityStore.fingerprint(peerCert) != expectedFingerprint) {
                    throw IOException("Cast connection from an unexpected certificate.")
                }
                val channel = CastChannel(socket)
                val presented = PairingProtocol.readString(channel.input)
                if (!fixedTimeEquals(presented, expectedToken)) {
                    throw IOException("Cast connection with the wrong token.")
                }
                socket.soTimeout = 0
                return channel
            } catch (e: Exception) {
                runCatching { socket.close() }
                throw e
            }
        }

        /** A fresh token for one cast: 32 bytes of CSPRNG output, base64url, no padding. */
        fun newToken(): String =
            Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(32).also { SecureRandom().nextBytes(it) })

        private fun fixedTimeEquals(a: String, b: String): Boolean {
            val x = a.toByteArray(Charsets.UTF_8)
            val y = b.toByteArray(Charsets.UTF_8)
            if (x.size != y.size) return false
            var diff = 0
            for (i in x.indices) diff = diff or (x[i].toInt() xor y[i].toInt())
            return diff == 0
        }
    }
}

/**
 * The receiving side's media listener: open only while one cast is being set up or running, on
 * an ephemeral port the receiver tells the sender over the session ([CastReady]). A connection
 * that fails the fingerprint or token check is closed and the listener goes on listening, so one
 * bad attempt cannot block the real sender.
 */
class CastChannelListener private constructor(
    private val serverSocket: SSLServerSocket,
    private val expectedToken: String,
    private val expectedFingerprint: String
) : Closeable {
    val port: Int get() = serverSocket.localPort

    /** Waits for the sender. Throws when a connection turned up and was refused, so the caller
     * can log it; the listener itself stays usable. */
    suspend fun accept(): CastChannel = withContext(Dispatchers.IO) {
        val socket = serverSocket.accept() as SSLSocket
        CastChannel.accept(socket, expectedToken, expectedFingerprint)
    }

    override fun close() {
        runCatching { serverSocket.close() }
    }

    companion object {
        fun start(
            identity: Pair<X509Certificate, PrivateKey>,
            expectedToken: String,
            expectedFingerprint: String,
            port: Int = 0
        ): CastChannelListener {
            val serverSocket = (PairingSession.buildSslContext(identity)
                .serverSocketFactory.createServerSocket(port) as SSLServerSocket)
                .apply { needClientAuth = true }
            return CastChannelListener(serverSocket, expectedToken, expectedFingerprint)
        }
    }
}
