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
    fun `chat send round trips`() {
        val out = ByteArrayOutputStream()
        val sent = ChatSend("msg-1", 1_760_000_000_000L, "hello world")
        MeshMessage.write(DataOutputStream(out), sent)

        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertTrue(received is ChatSend)
        received as ChatSend
        assertEquals(sent.id, received.id)
        assertEquals(sent.timestamp, received.timestamp)
        assertEquals("hello world", received.text)
    }

    @Test
    fun `chat edit round trips`() {
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), ChatEdit("msg-1", "edited text"))

        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertTrue(received is ChatEdit)
        received as ChatEdit
        assertEquals("msg-1", received.id)
        assertEquals("edited text", received.text)
    }

    @Test
    fun `chat delete round trips`() {
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), ChatDelete("msg-1"))

        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertTrue(received is ChatDelete)
        assertEquals("msg-1", (received as ChatDelete).id)
    }

    @Test
    fun `heartbeat round trips`() {
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), Heartbeat)

        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertTrue(received is Heartbeat)
    }

    @Test
    fun `chat history sync round trips`() {
        val out = ByteArrayOutputStream()
        val entries = listOf(
            ChatSyncEntry("msg-1", "self-uuid", 100L, "first"),
            ChatSyncEntry("msg-2", "peer-uuid", 200L, "second"),
        )
        MeshMessage.write(DataOutputStream(out), ChatHistorySync(entries))

        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertTrue(received is ChatHistorySync)
        received as ChatHistorySync
        assertEquals(entries, received.entries)
    }

    @Test
    fun `chat history sync empty round trips`() {
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), ChatHistorySync(emptyList()))

        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertTrue(received is ChatHistorySync)
        assertTrue((received as ChatHistorySync).entries.isEmpty())
    }

    @Test
    fun `multiple messages round trip in order on same stream`() {
        val out = ByteArrayOutputStream()
        val dos = DataOutputStream(out)
        MeshMessage.write(dos, ChatSend("msg-1", 1L, "first"))
        MeshMessage.write(dos, Heartbeat)
        MeshMessage.write(dos, ChatSend("msg-2", 2L, "second"))

        val input = DataInputStream(ByteArrayInputStream(out.toByteArray()))
        val first = MeshMessage.read(input) as ChatSend
        assertEquals("first", first.text)
        assertTrue(MeshMessage.read(input) is Heartbeat)
        val second = MeshMessage.read(input) as ChatSend
        assertEquals("second", second.text)
    }

    @Test
    fun `wire format matches Windows byte for byte for a known chat send`() {
        // Cross-platform contract check: a fixed input must produce this exact byte sequence,
        // independently matched against the equivalent Windows MeshMessageTests assertions.
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), ChatSend("", 1L, "hi"))
        val bytes = out.toByteArray()
        assertEquals(0x01, bytes[0].toInt()) // tag
        // bytes[1..4] = 4-byte big-endian length of the (empty) id string
        assertEquals(0, bytes[1].toInt()); assertEquals(0, bytes[2].toInt())
        assertEquals(0, bytes[3].toInt()); assertEquals(0, bytes[4].toInt())
        // bytes[5..12] = 8-byte big-endian timestamp = 1L
        assertEquals(0, bytes[5].toInt()); assertEquals(0, bytes[6].toInt())
        assertEquals(0, bytes[7].toInt()); assertEquals(0, bytes[8].toInt())
        assertEquals(0, bytes[9].toInt()); assertEquals(0, bytes[10].toInt())
        assertEquals(0, bytes[11].toInt()); assertEquals(1, bytes[12].toInt())
    }

    @Test
    fun `chat send larger than pairing string cap round trips`() {
        // Regression: the text field used to go through PairingProtocol.writeString, which caps
        // strings at 1024 bytes, so any real-sized message threw on write and wedged the session.
        val text = "chat-message-".repeat(500) + "é中😀"
        assertTrue(text.toByteArray(Charsets.UTF_8).size > PairingProtocol.MAX_STRING_BYTES)
        val out = ByteArrayOutputStream()
        MeshMessage.write(DataOutputStream(out), ChatSend("msg-1", 42L, text))

        val input = ByteArrayInputStream(out.toByteArray())
        val received = MeshMessage.read(DataInputStream(input)) as ChatSend

        assertEquals(42L, received.timestamp)
        assertEquals(text, received.text)
        assertEquals(0, input.available())
    }

    @Test
    fun `chat send length prefix above cap is rejected`() {
        val out = ByteArrayOutputStream()
        val dos = DataOutputStream(out)
        dos.writeByte(0x01)
        dos.writeInt(0) // empty id string
        dos.writeLong(0L)
        dos.writeInt(MeshMessage.MAX_TEXT_BYTES + 1)

        assertThrows(IOException::class.java) {
            MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))
        }
    }

    @Test
    fun `chat send text of max chars survives the wire`() {
        // The limit both apps show the user is in characters, the wire limit is in bytes. A text
        // at the character limit has to fit the byte limit even when every character is a 3-byte
        // one, or a message one side offers to send is one the other side drops.
        val text = "中".repeat(MeshMessage.MAX_TEXT_CHARS)
        val out = ByteArrayOutputStream()

        MeshMessage.write(DataOutputStream(out), ChatSend("msg-1", 42L, text))
        val received = MeshMessage.read(DataInputStream(ByteArrayInputStream(out.toByteArray())))

        assertEquals(text.length, (received as ChatSend).text.length)
    }
}
