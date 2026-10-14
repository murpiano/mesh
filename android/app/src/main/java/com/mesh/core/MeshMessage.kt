package com.mesh.core

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException

/**
 * Post-pairing wire messages. MUST stay wire-compatible with
 * src/windows/Mesh.Core/MeshMessage.cs.
 *
 *  1. tag         1 byte (via [PairingProtocol.writeByte]): 0x01 = ClipboardPush, 0x02 = Heartbeat
 *  2. timestamp   ClipboardPush only: 8 bytes, big-endian ms (DataOutputStream.writeLong)
 *  3. text        ClipboardPush only: length-prefixed UTF-8 (via [PairingProtocol.writeString])
 */
sealed class MeshMessage {
    companion object {
        private const val CLIPBOARD_PUSH_TAG = 0x01
        private const val HEARTBEAT_TAG = 0x02

        fun write(output: DataOutputStream, message: MeshMessage) {
            when (message) {
                is ClipboardPush -> {
                    PairingProtocol.writeByte(output, CLIPBOARD_PUSH_TAG)
                    output.writeLong(message.timestamp) // DataOutputStream.writeLong is big-endian
                    PairingProtocol.writeString(output, message.text)
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
                    val text = PairingProtocol.readString(input)
                    ClipboardPush(timestamp, text)
                }
                HEARTBEAT_TAG -> Heartbeat
                else -> throw EOFException("Unknown message tag: $tag")
            }
        }
    }
}

data class ClipboardPush(val timestamp: Long, val text: String) : MeshMessage()
object Heartbeat : MeshMessage()
