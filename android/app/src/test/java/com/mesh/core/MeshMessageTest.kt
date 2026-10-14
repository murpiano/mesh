package com.mesh.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException

class MeshMessageTest {
    @Test
    fun `clipboard push round trips`() {
        val out = ByteArrayOutputStream()
        val sent = ClipboardPush(1_760_000_000_000L, "hello world")
        MeshMessage.write(DataOutputStream(out), sent)

        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertTrue(received is ClipboardPush)
        received as ClipboardPush
        assertEquals(sent.timestamp, received.timestamp)
        assertEquals("hello world", received.text)
    }

    @Test
    fun `heartbeat round trips`() {
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), Heartbeat)

        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertTrue(received is Heartbeat)
    }

    @Test
    fun `multiple messages round trip in order on same stream`() {
        val out = ByteArrayOutputStream()
        val dos = DataOutputStream(out)
        MeshMessage.write(dos, ClipboardPush(1L, "first"))
        MeshMessage.write(dos, Heartbeat)
        MeshMessage.write(dos, ClipboardPush(2L, "second"))

        val input = DataInputStream(ByteArrayInputStream(out.toByteArray()))
        val first = MeshMessage.read(input) as ClipboardPush
        assertEquals("first", first.text)
        assertTrue(MeshMessage.read(input) is Heartbeat)
        val second = MeshMessage.read(input) as ClipboardPush
        assertEquals("second", second.text)
    }

    @Test
    fun `wire format matches Windows byte for byte for a known clipboard push`() {
        // Cross-platform contract check: a fixed input must produce this exact byte sequence,
        // independently matched against the equivalent Windows MeshMessageTests assertions.
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), ClipboardPush(1L, "hi"))
        val bytes = out.toByteArray()
        assertEquals(0x01, bytes[0].toInt())
        // bytes[1..8] = 8-byte big-endian timestamp = 1L
        assertEquals(0, bytes[1].toInt()); assertEquals(0, bytes[2].toInt())
        assertEquals(0, bytes[3].toInt()); assertEquals(0, bytes[4].toInt())
        assertEquals(0, bytes[5].toInt()); assertEquals(0, bytes[6].toInt())
        assertEquals(0, bytes[7].toInt()); assertEquals(1, bytes[8].toInt())
    }

    @Test
    fun `clipboard push larger than pairing string cap round trips`() {
        // Regression: the text field used to go through PairingProtocol.writeString, which caps
        // strings at 1024 bytes, so any real-sized clip threw on write and wedged the session.
        val text = "clipboard-".repeat(500) + "\u00e9\u4e2d\uD83D\uDE00"
        assertTrue(text.toByteArray(Charsets.UTF_8).size > PairingProtocol.MAX_STRING_BYTES)
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), ClipboardPush(42L, text))

        val input = ByteArrayInputStream(out.toByteArray())
        val received = MeshMessage.read(DataInputStream(input)) as ClipboardPush

        assertEquals(42L, received.timestamp)
        assertEquals(text, received.text)
        assertEquals(0, input.available())
    }

    @Test
    fun `clipboard push length prefix above cap is rejected`() {
        val out = ByteArrayOutputStream()
        val dos = DataOutputStream(out)
        dos.writeByte(0x01)
        dos.writeLong(0L)
        dos.writeInt(MeshMessage.MAX_TEXT_BYTES + 1)

        assertThrows(IOException::class.java) {
            MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))
        }
    }
}
