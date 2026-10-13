package com.mesh.core

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Post-TLS-handshake pairing protocol. MUST stay wire-compatible with
 * src/windows/Mesh.Core/PairingProtocol.cs.
 *
 * Both peers run the same symmetric sequence over the open TLS stream. Every step is
 * "send own value, then read peer's value", so neither side needs to know whether it is the
 * TCP initiator or the listener:
 *
 *  1. uuid        string: u32 big-endian byte length + UTF-8 bytes (max 1024 bytes)
 *  2. name        string: same encoding
 *  3. trusted     1 byte: 0x01 if this side has a stored record for the peer's uuid whose
 *                 fingerprint equals the live TLS peer fingerprint, else 0x00. If BOTH sides
 *                 sent 0x01 this is a trusted reconnect and the protocol ends here (no PIN).
 *  4. commitment  32 raw bytes: SHA-256(own 16-byte random nonce)
 *  5. nonce       16 raw bytes, sent only after the peer's commitment has been received.
 *                 Receiver verifies SHA-256(nonce) == commitment; mismatch aborts pairing.
 *  -- both sides compute the PIN ([PinDerivation]) and ask their user --
 *  6. confirm     1 byte: 0x01 = Match, 0x00 = Not matching / declined / timed out.
 *                 Pairing succeeds only if both sides sent 0x01.
 *
 * Steps 1-5 use the 10 s network timeout; step 6 uses a separate 60 s user-interaction budget
 * for the local prompt plus 60 s for waiting on the peer's byte.
 */
object PairingProtocol {
    const val MAX_STRING_BYTES = 1024
    const val COMMITMENT_LENGTH = 32
    const val YES: Int = 0x01
    const val NO: Int = 0x00

    private val random = SecureRandom()

    fun writeString(out: DataOutputStream, value: String) {
        val bytes = value.toByteArray(Charsets.UTF_8)
        require(bytes.size <= MAX_STRING_BYTES) { "String exceeds $MAX_STRING_BYTES bytes." }
        out.writeInt(bytes.size) // DataOutputStream.writeInt is big-endian
        out.write(bytes)
        out.flush()
    }

    fun readString(input: DataInputStream): String {
        val length = input.readInt().toLong() and 0xFFFFFFFFL
        if (length > MAX_STRING_BYTES) {
            throw IOException("Peer sent a string of $length bytes (max $MAX_STRING_BYTES).")
        }
        return String(readBytes(input, length.toInt()), Charsets.UTF_8)
    }

    fun writeBytes(out: DataOutputStream, bytes: ByteArray) {
        out.write(bytes)
        out.flush()
    }

    /** Throws [java.io.EOFException] if the peer closes early. */
    fun readBytes(input: DataInputStream, count: Int): ByteArray =
        ByteArray(count).also { input.readFully(it) }

    fun writeByte(out: DataOutputStream, value: Int) {
        out.writeByte(value)
        out.flush()
    }

    fun readByte(input: DataInputStream): Int = input.readUnsignedByte()

    fun createNonce(): ByteArray = ByteArray(PinDerivation.NONCE_LENGTH).also { random.nextBytes(it) }

    fun commit(nonce: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(nonce)

    fun verifyCommitment(commitment: ByteArray, revealedNonce: ByteArray): Boolean =
        revealedNonce.size == PinDerivation.NONCE_LENGTH &&
            MessageDigest.isEqual(commitment, commit(revealedNonce))
}
