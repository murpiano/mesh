package com.mesh.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * Test vectors are shared verbatim with src/windows/Mesh.Tests/PinDerivationTests.cs and were
 * independently cross-checked with a standalone Python implementation of the algorithm.
 */
class PinDerivationTest {
    private val fpA = "a1b2c3d4e5f6071829a1b2c3d4e5f6071829a1b2c3d4e5f6071829a1b2c3d4e5"
    private val fpB = "0123456789abcdef110123456789abcdef110123456789abcdef110123456789"
    private val fpF = "f".repeat(64)
    private val fpZero = "0".repeat(64)

    private val nonce1 = ByteArray(16) { it.toByte() }                 // 0x00..0x0f
    private val nonce2 = ByteArray(16) { (0xf0 + it).toByte() }        // 0xf0..0xff
    private val nonceZero = ByteArray(16)
    private val nonceFf = ByteArray(16) { 0xff.toByte() }

    @Test
    fun `derive matches known vector and is order independent`() {
        assertEquals("016425", PinDerivation.derive(fpA, fpB, nonce1, nonce2))
        assertEquals("016425", PinDerivation.derive(fpB, fpA, nonce2, nonce1))
        // Fingerprints and nonces are sorted independently.
        assertEquals("016425", PinDerivation.derive(fpA, fpB, nonce2, nonce1))
    }

    @Test
    fun `derive matches second known vector`() {
        assertEquals("250919", PinDerivation.derive(fpF, fpZero, nonceZero, nonceFf))
    }

    @Test
    fun `derive is sensitive to the full fingerprint not just a prefix`() {
        val fpA2 = fpA.take(63) + "6" // identical except for the very last hex char
        assertEquals("122587", PinDerivation.derive(fpA2, fpB, nonce1, nonce2))
        assertNotEquals(
            PinDerivation.derive(fpA, fpB, nonce1, nonce2),
            PinDerivation.derive(fpA2, fpB, nonce1, nonce2))
    }

    @Test
    fun `derive is sensitive to nonces`() {
        val nonce1b = nonce1.copyOf().also { it[15] = 0x10 }
        assertEquals("913817", PinDerivation.derive(fpA, fpB, nonce1b, nonce2))
        assertNotEquals(
            PinDerivation.derive(fpA, fpB, nonce1, nonce2),
            PinDerivation.derive(fpA, fpB, nonce1b, nonce2))
    }

    @Test
    fun `derive is always six digits`() {
        assertEquals(6, PinDerivation.derive(fpA, fpF, nonce1, nonce2).length)
    }

    @Test(expected = IllegalArgumentException::class)
    fun `derive rejects wrong nonce length`() {
        PinDerivation.derive(fpA, fpB, ByteArray(15), nonce2)
    }
}
