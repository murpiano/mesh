package com.mesh.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException

class PairingProtocolTest {
    @Test
    fun `string round trips with big endian length prefix`() {
        val buffer = ByteArrayOutputStream()
        PairingProtocol.writeString(DataOutputStream(buffer), "Pixel 8 — Δ")
        val bytes = buffer.toByteArray()
        // 14 UTF-8 bytes ("—" = 3, "Δ" = 2): header 00 00 00 0E — same as the Windows test.
        assertArrayEquals(byteArrayOf(0, 0, 0, 14), bytes.copyOfRange(0, 4))
        assertEquals(18, bytes.size)
        assertEquals("Pixel 8 — Δ", PairingProtocol.readString(DataInputStream(ByteArrayInputStream(bytes))))
    }

    @Test(expected = IOException::class)
    fun `readString rejects oversized length`() {
        PairingProtocol.readString(DataInputStream(ByteArrayInputStream(byteArrayOf(0x00, 0x10, 0x00, 0x00))))
    }

    @Test(expected = EOFException::class)
    fun `readBytes throws on early eof`() {
        PairingProtocol.readBytes(DataInputStream(ByteArrayInputStream(byteArrayOf(1, 2))), 4)
    }

    @Test
    fun `commitment is sha256 of nonce known vector`() {
        val nonce = ByteArray(16) { it.toByte() }
        assertEquals(
            "be45cb2605bf36bebde684841a28f0fd43c69850a3dce5fedba69928ee3a8991",
            PairingProtocol.commit(nonce).toLowerHex())
    }

    @Test
    fun `verifyCommitment accepts matching nonce and rejects others`() {
        val nonce = PairingProtocol.createNonce()
        assertEquals(16, nonce.size)
        val commitment = PairingProtocol.commit(nonce)
        assertTrue(PairingProtocol.verifyCommitment(commitment, nonce))
        val tampered = nonce.copyOf().also { it[0] = (it[0].toInt() xor 1).toByte() }
        assertFalse(PairingProtocol.verifyCommitment(commitment, tampered))
        assertFalse(PairingProtocol.verifyCommitment(commitment, ByteArray(15)))
    }

    @Test
    fun `createNonce is random`() {
        assertFalse(PairingProtocol.createNonce().contentEquals(PairingProtocol.createNonce()))
    }
}
