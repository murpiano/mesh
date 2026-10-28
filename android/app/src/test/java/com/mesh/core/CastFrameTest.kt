package com.mesh.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException

class CastFrameTest {
    @Test
    fun `every frame type round trips`() {
        for (type in CastFrameType.entries) {
            val out = ByteArrayOutputStream()
            val payload = byteArrayOf(1, 2, 3, 4, 5)
            CastFrame.write(DataOutputStream(out), CastFrame(type, 1_234_567_890L, payload))

            val received = CastFrame.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

            assertEquals(type, received.type)
            assertEquals(1_234_567_890L, received.timestampUs)
            assertArrayEquals(payload, received.payload)
        }
    }

    @Test
    fun `payload at the limit round trips`() {
        val out = ByteArrayOutputStream()
        val payload = ByteArray(CastFrame.MAX_PAYLOAD_BYTES)
        payload[payload.size - 1] = 42
        CastFrame.write(DataOutputStream(out), CastFrame(CastFrameType.VIDEO_KEY, 0, payload))

        val received = CastFrame.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertEquals(CastFrame.MAX_PAYLOAD_BYTES, received.payload.size)
        assertEquals(42.toByte(), received.payload[received.payload.size - 1])
    }

    @Test
    fun `payload over the limit is rejected without allocating`() {
        val out = ByteArrayOutputStream()
        val header = DataOutputStream(out)
        header.writeByte(CastFrameType.VIDEO_DELTA.tag)
        header.writeLong(0)
        header.writeInt(CastFrame.MAX_PAYLOAD_BYTES + 1)

        assertThrows(IOException::class.java) {
            CastFrame.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))
        }
    }

    @Test
    fun `unknown frame type is rejected`() {
        val out = ByteArrayOutputStream()
        val header = DataOutputStream(out)
        header.writeByte(0x7F)
        header.writeLong(0)
        header.writeInt(0)

        assertThrows(IOException::class.java) {
            CastFrame.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))
        }
    }
}
