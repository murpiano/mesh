package com.mesh.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class PinDerivationTest {
    private val fpA = "a1b2c3d4e5f6071829a1b2c3d4e5f6071829a1b2c3d4e5f6071829a1b2c3d4e5"
    private val fpB = "0123456789abcdef110123456789abcdef110123456789abcdef110123456789".take(64)
    private val fpF = "f".repeat(64)
    private val fpZero = "0".repeat(64)

    @Test
    fun `derive is order independent for known vector`() {
        assertEquals("936941", PinDerivation.derive(fpA, fpB))
        assertEquals("936941", PinDerivation.derive(fpB, fpA))
    }

    @Test
    fun `derive matches second known vector`() {
        assertEquals("859565", PinDerivation.derive(fpF, fpZero))
    }

    @Test
    fun `derive differs for different fingerprints`() {
        val pin1 = PinDerivation.derive(fpA, fpB)
        val pin2 = PinDerivation.derive(fpA, fpF)
        assertNotEquals(pin1, pin2)
    }

    @Test
    fun `derive is always six digits`() {
        assertEquals(6, PinDerivation.derive(fpA, fpF).length)
    }
}
