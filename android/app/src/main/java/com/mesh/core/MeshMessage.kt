package com.mesh.core

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException

/**
 * Post-pairing wire messages. MUST stay wire-compatible with
 * src/windows/Mesh.Core/MeshMessage.cs.
 *
 *  1. tag         1 byte (via [PairingProtocol.writeByte]): 0x01 = ClipboardPush, 0x02 = Heartbeat
 *  2. timestamp   ClipboardPush only: 8 bytes, big-endian ms (DataOutputStream.writeLong)
 *  3. text        ClipboardPush only: u32 big-endian byte length + UTF-8 bytes, max [MAX_TEXT_BYTES].
 *                 Self-contained framing (same shape as PairingProtocol's strings) so the
 *                 1024-byte handshake-string cap does not apply to clipboard text.
 */
sealed class MeshMessage {
    companion object {
        private const val CLIPBOARD_PUSH_TAG = 0x01
        private const val HEARTBEAT_TAG = 0x02

        /** 1 MiB of UTF-16 chars expands to at most 4 bytes per char in UTF-8. */
        const val MAX_TEXT_BYTES = 4 * 1_048_576

        fun write(output: DataOutputStream, message: MeshMessage) {
            when (message) {
                is ClipboardPush -> {
                    PairingProtocol.writeByte(output, CLIPBOARD_PUSH_TAG)
                    output.writeLong(message.timestamp) // DataOutputStream.writeLong is big-endian
                    writeText(output, message.text)
                }
                is Heartbeat -> PairingProtocol.writeByte(output, HEARTBEAT_TAG)
            }
            output.flush()
        }

        fun read(input: DataInputStream): MeshMessage {
            val tag = PairingProtocol.readByte(input)
            return when (tag) {
                CLIPBOARD_PUSH_TAG -> {
                    val timestamp = input.readLong()
                    val text = readText(input)
                    ClipboardPush(timestamp, text)
                }
                HEARTBEAT_TAG -> Heartbeat
                else -> throw EOFException("Unknown message tag: $tag")
            }
        }

        private fun writeText(output: DataOutputStream, text: String) {
            val bytes = text.toByteArray(Charsets.UTF_8)
            require(bytes.size <= MAX_TEXT_BYTES) {
                "ClipboardPush text is ${bytes.size} bytes (max $MAX_TEXT_BYTES)."
            }
            output.writeInt(bytes.size) // DataOutputStream.writeInt is big-endian
            output.write(bytes)
        }

        private fun readText(input: DataInputStream): String {
            val length = input.readInt().toLong() and 0xFFFFFFFFL
            // Validate before allocating so a hostile length prefix cannot force a huge buffer.
            if (length > MAX_TEXT_BYTES) {
                throw IOException("Peer sent ClipboardPush text of $length bytes (max $MAX_TEXT_BYTES).")
            }
            val bytes = ByteArray(length.toInt())
            input.readFully(bytes)
            return String(bytes, Charsets.UTF_8)
        }
    }
}

data class ClipboardPush(val timestamp: Long, val text: String) : MeshMessage()
object Heartbeat : MeshMessage()
