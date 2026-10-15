package com.mesh.core

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException

/**
 * Post-pairing wire messages. MUST stay wire-compatible with
 * src/windows/Mesh.Core/MeshMessage.cs.
 *
 *  1. tag              1 byte (via [PairingProtocol.writeByte]): 0x01 = ChatSend,
 *                       0x02 = Heartbeat, 0x03 = Goodbye, 0x04 = ChatHistorySync
 *  2. id                ChatSend only: short string (via [PairingProtocol.writeString])
 *  3. timestamp         ChatSend only: 8 bytes, big-endian ms (DataOutputStream.writeLong)
 *  4. text              ChatSend only: u32 big-endian byte length + UTF-8 bytes, max
 *                       [MAX_TEXT_BYTES]. Self-contained framing (same shape as
 *                       PairingProtocol's strings) so the 1024-byte handshake-string cap does
 *                       not apply to chat text.
 *  5. ChatHistorySync   u32 entry count, then per entry: id (short string), senderUuid (short
 *                       string), timestamp (8 bytes), text (as above)
 */
sealed class MeshMessage {
    companion object {
        private const val CHAT_SEND_TAG = 0x01
        private const val HEARTBEAT_TAG = 0x02
        private const val GOODBYE_TAG = 0x03
        private const val CHAT_HISTORY_SYNC_TAG = 0x04
        private const val CHAT_EDIT_TAG = 0x05
        private const val CHAT_DELETE_TAG = 0x06

        /**
         * 1 MiB of UTF-16 code units expands to at most 3 bytes per code unit in UTF-8 (a
         * surrogate pair contributes 4 bytes across 2 code units, not 4 bytes per single code
         * unit) — 4x gives comfortable headroom over that worst case.
         */
        const val MAX_TEXT_BYTES = 4 * 1_048_576

        /** Generous relative to the 50-per-peer cap each side actually stores, so a sync can
         * never be truncated. */
        const val MAX_HISTORY_SYNC_ENTRIES = 1000

        fun write(output: DataOutputStream, message: MeshMessage) {
            when (message) {
                is ChatSend -> {
                    PairingProtocol.writeByte(output, CHAT_SEND_TAG)
                    PairingProtocol.writeString(output, message.id)
                    output.writeLong(message.timestamp) // DataOutputStream.writeLong is big-endian
                    writeText(output, message.text)
                }
                is Heartbeat -> PairingProtocol.writeByte(output, HEARTBEAT_TAG)
                is Goodbye -> PairingProtocol.writeByte(output, GOODBYE_TAG)
                is ChatHistorySync -> {
                    PairingProtocol.writeByte(output, CHAT_HISTORY_SYNC_TAG)
                    require(message.entries.size <= MAX_HISTORY_SYNC_ENTRIES) {
                        "History sync has ${message.entries.size} entries (max $MAX_HISTORY_SYNC_ENTRIES)."
                    }
                    output.writeInt(message.entries.size)
                    for (entry in message.entries) {
                        PairingProtocol.writeString(output, entry.id)
                        PairingProtocol.writeString(output, entry.senderUuid)
                        output.writeLong(entry.timestamp)
                        writeText(output, entry.text)
                    }
                }
                is ChatEdit -> {
                    PairingProtocol.writeByte(output, CHAT_EDIT_TAG)
                    PairingProtocol.writeString(output, message.id)
                    writeText(output, message.text)
                }
                is ChatDelete -> {
                    PairingProtocol.writeByte(output, CHAT_DELETE_TAG)
                    PairingProtocol.writeString(output, message.id)
                }
            }
            output.flush()
        }

        fun read(input: DataInputStream): MeshMessage {
            val tag = PairingProtocol.readByte(input)
            return when (tag) {
                CHAT_SEND_TAG -> {
                    val id = PairingProtocol.readString(input)
                    val timestamp = input.readLong()
                    val text = readText(input)
                    ChatSend(id, timestamp, text)
                }
                HEARTBEAT_TAG -> Heartbeat
                GOODBYE_TAG -> Goodbye
                CHAT_HISTORY_SYNC_TAG -> {
                    val count = input.readInt().toLong() and 0xFFFFFFFFL
                    if (count > MAX_HISTORY_SYNC_ENTRIES) {
                        throw IOException("Peer sent a history sync of $count entries (max $MAX_HISTORY_SYNC_ENTRIES).")
                    }
                    val entries = (0 until count).map {
                        val id = PairingProtocol.readString(input)
                        val senderUuid = PairingProtocol.readString(input)
                        val timestamp = input.readLong()
                        val text = readText(input)
                        ChatSyncEntry(id, senderUuid, timestamp, text)
                    }
                    ChatHistorySync(entries)
                }
                CHAT_EDIT_TAG -> {
                    val id = PairingProtocol.readString(input)
                    val text = readText(input)
                    ChatEdit(id, text)
                }
                CHAT_DELETE_TAG -> ChatDelete(PairingProtocol.readString(input))
                else -> throw EOFException("Unknown message tag: $tag")
            }
        }

        private fun writeText(output: DataOutputStream, text: String) {
            val bytes = text.toByteArray(Charsets.UTF_8)
            require(bytes.size <= MAX_TEXT_BYTES) {
                "Chat message text is ${bytes.size} bytes (max $MAX_TEXT_BYTES)."
            }
            output.writeInt(bytes.size) // DataOutputStream.writeInt is big-endian
            output.write(bytes)
        }

        private fun readText(input: DataInputStream): String {
            val length = input.readInt().toLong() and 0xFFFFFFFFL
            // Validate before allocating so a hostile length prefix cannot force a huge buffer.
            if (length > MAX_TEXT_BYTES) {
                throw IOException("Peer sent chat message text of $length bytes (max $MAX_TEXT_BYTES).")
            }
            val bytes = ByteArray(length.toInt())
            input.readFully(bytes)
            return String(bytes, Charsets.UTF_8)
        }
    }
}

/** A single new chat message, sent live while both sides are connected. The sender's identity
 * is always the session's peer from the receiver's point of view, so it isn't carried on the
 * wire here (unlike [ChatSyncEntry], which can describe either side). */
data class ChatSend(val id: String, val timestamp: Long, val text: String) : MeshMessage()

object Heartbeat : MeshMessage()

/** Sent once, right before an intentional (user-initiated) disconnect, so the peer stops its
 * own reconnect loop too instead of dialing back in later and silently undoing it. */
object Goodbye : MeshMessage()

/** Sent once, right after a mesh session attaches (both directions, independently): the
 * sender's own locally-stored history for this conversation, so the other side can merge in
 * anything it's missing — e.g. after it unpaired and re-paired, losing its local copy. */
data class ChatHistorySync(val entries: List<ChatSyncEntry>) : MeshMessage()

/** [senderUuid] is a device uuid (either side's), not perspective-relative, so the receiver can
 * tell whether an entry replayed back to it is one it originally sent. */
data class ChatSyncEntry(val id: String, val senderUuid: String, val timestamp: Long, val text: String)

/** Edits an own message already seen by the peer. Sent live only while connected to that peer;
 * if disconnected, the local store still changes but the peer is not retried later. */
data class ChatEdit(val id: String, val text: String) : MeshMessage()

/** Deletes an own message already seen by the peer. Same best-effort delivery caveat as
 * [ChatEdit]. */
data class ChatDelete(val id: String) : MeshMessage()
