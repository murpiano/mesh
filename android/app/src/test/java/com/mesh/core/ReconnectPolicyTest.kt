package com.mesh.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.UUID

class ReconnectPolicyTest {
    @Test
    fun exactlyOneSideDialsForDistinctUuids() {
        repeat(1_000) {
            val a = UUID.randomUUID().toString()
            val b = UUID.randomUUID().toString()
            if (a == b) return@repeat
            assertNotEquals(ReconnectPolicy.isDialer(a, b), ReconnectPolicy.isDialer(b, a))
        }
    }

    @Test
    fun lowerUuidDialsUsingOrdinalOrder() {
        // Same answers .NET's string.CompareOrdinal gives: digits (0x30-0x39) sort before
        // lowercase hex letters (0x61-0x66); comparison stops at the first differing code unit.
        assertTrue(ReconnectPolicy.isDialer(
            "0f8fad5b-d9cb-469f-a165-70867728950e", "7c9e6679-7425-40de-944b-e07fc1f90ae7"))
        assertTrue(ReconnectPolicy.isDialer(
            "9fffffff-ffff-ffff-ffff-ffffffffffff", "a0000000-0000-0000-0000-000000000000"))
        assertFalse(ReconnectPolicy.isDialer(
            "7c9e6679-7425-40de-944b-e07fc1f90ae8", "7c9e6679-7425-40de-944b-e07fc1f90ae7"))
        assertFalse(ReconnectPolicy.isDialer(
            "7c9e6679-7425-40de-944b-e07fc1f90ae7", "7c9e6679-7425-40de-944b-e07fc1f90ae7"))
    }

    @Test
    fun comparisonIsCaseSensitiveLikeCompareOrdinal() {
        // Not culture/case-insensitive: 'A' (0x41) < 'a' (0x61).
        assertTrue(ReconnectPolicy.isDialer("A", "a"))
        assertFalse(ReconnectPolicy.isDialer("a", "A"))
    }

    @Test
    fun generatedUuidsAreCanonicalLowercase() {
        // Matches Windows' Guid.ToString() ("D" format, lowercase), so the two platforms'
        // identifiers sort consistently against each other.
        val uuid = UUID.randomUUID().toString()
        assertEquals(36, uuid.length)
        assertEquals(uuid.lowercase(), uuid)
    }

    @Test
    fun dialerFollowsBackoffThenCapsAt30s() {
        val delays = (0..7).map { ReconnectPolicy.delayBeforeAttemptMs(isDialer = true, attempt = it) }
        assertEquals(listOf(1_000L, 2_000L, 4_000L, 8_000L, 16_000L, 30_000L, 30_000L, 30_000L), delays)
    }

    @Test
    fun passiveSideWaitsGraceThenFallbackInterval() {
        val delays = (0..3).map { ReconnectPolicy.delayBeforeAttemptMs(isDialer = false, attempt = it) }
        assertEquals(listOf(120_000L, 45_000L, 45_000L, 45_000L), delays)
    }
}
