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
 *                       0x02 = Heartbeat, 0x03 = Goodbye, 0x04 = ChatHistorySync,
 *                       0x05 = ChatEdit, 0x06 = ChatDelete, 0x07 = CastStart,
 *                       0x08 = CastReady, 0x09 = CastStop
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
        private const val CAST_START_TAG = 0x07
        private const val CAST_READY_TAG = 0x08
        private const val CAST_STOP_TAG = 0x09

        /**
         * 1 MiB of UTF-16 code units expands to at most 3 bytes per code unit in UTF-8 (a
         * surrogate pair contributes 4 bytes across 2 code units, not 4 bytes per single code
         * unit) — 4x gives comfortable headroom over that worst case.
         */
        const val MAX_TEXT_BYTES = 4 * 1_048_576

        /**
         * Upper bound on a chat message's length in UTF-16 code units — what both apps check
         * before storing or sending, so neither offers to send what the other would drop.
         * Derived from [MAX_TEXT_BYTES] rather than written out, because a text this long always
         * fits the wire limit (see the 3-bytes-per-code-unit worst case above).
         */
        const val MAX_TEXT_CHARS = MAX_TEXT_BYTES / 4

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
                is CastStart -> {
                    PairingProtocol.writeByte(output, CAST_START_TAG)
                    PairingProtocol.writeString(output, message.castId)
                }
                is CastReady -> {
                    PairingProtocol.writeByte(output, CAST_READY_TAG)
                    PairingProtocol.writeString(output, message.castId)
                    writePort(output, message.port)
                    PairingProtocol.writeString(output, message.token)
                }
                is CastStop -> {
                    PairingProtocol.writeByte(output, CAST_STOP_TAG)
                    PairingProtocol.writeString(output, message.castId)
                    PairingProtocol.writeString(output, message.reason)
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
                CAST_START_TAG -> CastStart(PairingProtocol.readString(input))
                CAST_READY_TAG -> {
                    val castId = PairingProtocol.readString(input)
                    val port = readPort(input)
                    CastReady(castId, port, PairingProtocol.readString(input))
                }
                CAST_STOP_TAG -> CastStop(PairingProtocol.readString(input), PairingProtocol.readString(input))
                else -> throw EOFException("Unknown message tag: $tag")
            }
        }

        private fun writePort(output: DataOutputStream, port: Int) {
            require(port in 1..65535) { "Port $port out of range." }
            output.writeShort(port) // DataOutputStream.writeShort is big-endian
        }

        private fun readPort(input: DataInputStream): Int = input.readUnsignedShort()

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

/** Sent by the device that wants to cast its screen: "I am about to send you a picture". The
 * receiver answers with [CastReady]. The cast id ties the start, the settings and the stop of
 * one cast together, so a late message from a cast that already ended is easy to drop. */
data class CastStart(val castId: String) : MeshMessage()

/** The receiver's answer: the port its media-plane listener is on, and the one-time token the
 * sender has to present there (see CastChannel). The token is what keeps another device on the
 * network from walking into a listener that is briefly open. */
data class CastReady(val castId: String, val port: Int, val token: String) : MeshMessage()

/** Ends a cast, from either side. [reason] is shown to the other user when it is not empty —
 * an empty one means "the person on this side stopped it". */
data class CastStop(val castId: String, val reason: String) : MeshMessage()

/** Edits an own message already seen by the peer. Sent live only while connected to that peer;
 * if disconnected, the local store still changes but the peer is not retried later. */
data class ChatEdit(val id: String, val text: String) : MeshMessage()

/** Deletes an own message already seen by the peer. Same best-effort delivery caveat as
 * [ChatEdit]. */
data class ChatDelete(val id: String) : MeshMessage()
