package com.mesh.core

import java.security.MessageDigest

/**
 * 6-digit pairing PIN derivation. MUST stay byte-for-byte identical to
 * src/windows/Mesh.Core/PinDerivation.cs (see plan Global Constraints).
 *
 * Inputs: both peers' certificate SHA-256 fingerprints (64-char lowercase hex, no separators)
 * and both peers' 16-byte pairing nonces (exchanged via commit-then-reveal, see [PairingProtocol]).
 *
 * Algorithm:
 *  1. Sort the two fingerprint strings ascending (ordinal) -> f0, f1.
 *  2. Hex-encode each nonce as 32 lowercase hex chars, sort those two strings ascending
 *     (ordinal) -> n0, n1. Fingerprints and nonces are sorted independently.
 *  3. message = UTF-8(f0 + f1 + n0 + n1)  (64 + 64 + 32 + 32 = 192 ASCII bytes).
 *  4. hash = SHA-256(message).
 *  5. num = ((hash[0] shl 16) or (hash[1] shl 8) or hash[2]) % 1_000_000  (bytes unsigned).
 *  6. PIN = num formatted as 6-digit zero-padded decimal.
 *
 * Mixing in the committed nonces means an active man-in-the-middle cannot pre-compute a
 * certificate whose PIN collides with both honest peers' PINs: each honest side's nonce is
 * unknown to the attacker at the time the attacker must commit to its own.
 */
object PinDerivation {
    const val NONCE_LENGTH = 16

    fun derive(fingerprintA: String, fingerprintB: String, nonceA: ByteArray, nonceB: ByteArray): String {
        require(nonceA.size == NONCE_LENGTH && nonceB.size == NONCE_LENGTH) {
            "Nonces must be exactly $NONCE_LENGTH bytes."
        }
        val fps = listOf(fingerprintA, fingerprintB).sorted()
        val nonces = listOf(nonceA.toLowerHex(), nonceB.toLowerHex()).sorted()
        val combined = (fps[0] + fps[1] + nonces[0] + nonces[1]).toByteArray(Charsets.UTF_8)
        val hash = MessageDigest.getInstance("SHA-256").digest(combined)
        val num = (((hash[0].toInt() and 0xFF) shl 16) or
                   ((hash[1].toInt() and 0xFF) shl 8) or
                   (hash[2].toInt() and 0xFF)) % 1_000_000
        return num.toString().padStart(6, '0')
    }
}

internal fun ByteArray.toLowerHex(): String = joinToString("") { "%02x".format(it) }
