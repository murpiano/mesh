package com.mesh.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream

class CastSettingsTest {
    private fun sample() = CastSettings(
        quality = 73,
        audio = true,
        placement = CastPlacement(CastPlacementMode.WINDOW, "monitor-2", 10, 20, 1280, 720),
        monitors = listOf(
            CastMonitor("monitor-1", "Dell U2720Q", 3840, 2160, true),
            CastMonitor("monitor-2", "Laptop", 1920, 1080, false)
        )
    )

    private fun roundTrip(settings: CastSettings): CastSettings {
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), CastSettingsUpdate("cast-1", settings))
        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))
        assertTrue(received is CastSettingsUpdate)
        return (received as CastSettingsUpdate).settings
    }

    @Test
    fun `every field round trips`() {
        val sent = sample()
        val received = roundTrip(sent)

        assertEquals(sent.quality, received.quality)
        assertEquals(sent.audio, received.audio)
        assertEquals(sent.placement, received.placement)
        assertEquals(sent.monitors, received.monitors)
    }

    @Test
    fun `quality is clamped on read`() {
        assertEquals(0, roundTrip(sample().copy(quality = -5)).quality)
        assertEquals(100, roundTrip(sample().copy(quality = 250)).quality)
        assertEquals(50, roundTrip(sample().copy(quality = 50)).quality)
    }

    @Test
    fun `monitor list longer than the cap is rejected`() {
        val monitors = (0..CastSettings.MAX_MONITORS).map {
            CastMonitor("monitor-$it", "Screen $it", 1920, 1080, it == 0)
        }

        assertThrows(IllegalArgumentException::class.java) {
            MeshMessage.write(
                DataOutputStream(ByteArrayOutputStream()),
                CastSettingsUpdate("cast-1", sample().copy(monitors = monitors))
            )
        }
    }
}
