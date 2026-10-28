package com.mesh.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class CastQualityTest {
    private val screenWidth = 1080
    private val screenHeight = 2400

    @Test
    fun `the smooth end runs at full frame rate and scales down`() {
        val profile = CastQuality.profileFor(0, screenWidth, screenHeight)

        assertEquals(60, profile.frameRate)
        assertEquals(1920, maxOf(profile.width, profile.height))
        assertEquals(2, profile.keyframeIntervalSeconds)
    }

    @Test
    fun `the sharp end keeps the native size and few frames`() {
        val profile = CastQuality.profileFor(100, screenWidth, screenHeight)

        assertEquals(10, profile.frameRate)
        assertEquals(screenWidth, profile.width)
        assertEquals(screenHeight, profile.height)
        assertEquals(1, profile.keyframeIntervalSeconds)
    }

    @Test
    fun `frame rate never rises as the scale moves towards sharp`() {
        var previous = Int.MAX_VALUE
        for (quality in 0..100) {
            val frameRate = CastQuality.profileFor(quality, screenWidth, screenHeight).frameRate
            assertTrue("frame rate rose at quality $quality", frameRate <= previous)
            previous = frameRate
        }
    }

    @Test
    fun `bitrate stays within the cap and the picture stays encodable`() {
        for (quality in 0..100) {
            val profile = CastQuality.profileFor(quality, screenWidth, screenHeight)
            assertTrue("bitrate too high at $quality", profile.bitrate <= CastQuality.MAX_BITRATE)
            assertTrue("bitrate too low at $quality", profile.bitrate >= CastQuality.MIN_BITRATE)
            // H.264 wants even dimensions; an odd one is rejected by some encoders outright.
            assertEquals("odd width at $quality", 0, profile.width % 2)
            assertEquals("odd height at $quality", 0, profile.height % 2)
        }
    }

    @Test
    fun `a screen smaller than the cap is never scaled up`() {
        val profile = CastQuality.profileFor(0, 720, 1280)

        assertEquals(720, profile.width)
        assertEquals(1280, profile.height)
    }

    @Test
    fun `a quality outside the range is clamped`() {
        assertEquals(
            CastQuality.profileFor(0, screenWidth, screenHeight),
            CastQuality.profileFor(-20, screenWidth, screenHeight))
        assertEquals(
            CastQuality.profileFor(100, screenWidth, screenHeight),
            CastQuality.profileFor(250, screenWidth, screenHeight))
    }
}
