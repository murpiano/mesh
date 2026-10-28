package com.mesh.core

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException

/** What one media-plane frame carries. */
enum class CastFrameType(val tag: Int) {
    /** The video codec's configuration (H.264 SPS/PPS). Sent before the first picture and again
     * whenever the encoder restarts — a resolution change or a rotation. */
    VIDEO_CONFIG(1),

    /** A keyframe: decodable on its own, which is what a receiver that just connected waits
     * for. */
    VIDEO_KEY(2),
    VIDEO_DELTA(3),

    /** The audio codec's configuration (AAC). Sent when audio is switched on. */
    AUDIO_CONFIG(4),
    AUDIO_DATA(5);

    companion object {
        fun fromTag(tag: Int): CastFrameType =
            entries.firstOrNull { it.tag == tag } ?: throw IOException("Peer sent an unknown cast frame type: $tag.")
    }
}

/**
 * One frame on the media plane: the second connection of a cast, which carries nothing but
 * these. The control plane ([MeshMessage]) never carries a frame, and this never carries a
 * control message — that separation is what keeps a heartbeat from waiting behind a picture.
 *
 * MUST stay wire-compatible with src/windows/Mesh.Core/CastFrame.cs: 1 byte type, 8 bytes
 * big-endian microsecond timestamp, 4 bytes big-endian payload length, payload.
 */
data class CastFrame(val type: CastFrameType, val timestampUs: Long, val payload: ByteArray) {
    // data class with a ByteArray: generated equals/hashCode would compare by identity.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is CastFrame) return false
        return type == other.type && timestampUs == other.timestampUs && payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int =
        (type.hashCode() * 31 + timestampUs.hashCode()) * 31 + payload.contentHashCode()

    companion object {
        /** Far above any single encoded frame at the bitrates this uses, and small enough that a
         * hostile length prefix cannot make the receiver allocate its way out of memory. */
        const val MAX_PAYLOAD_BYTES = 8 * 1_048_576

        fun write(output: DataOutputStream, frame: CastFrame) {
            require(frame.payload.size <= MAX_PAYLOAD_BYTES) {
                "Cast frame payload is ${frame.payload.size} bytes (max $MAX_PAYLOAD_BYTES)."
            }
            output.writeByte(frame.type.tag)
            output.writeLong(frame.timestampUs) // DataOutputStream is big-endian
            output.writeInt(frame.payload.size)
            output.write(frame.payload)
            output.flush()
        }

        fun read(input: DataInputStream): CastFrame {
            val type = CastFrameType.fromTag(input.readUnsignedByte())
            val timestampUs = input.readLong()
            val length = input.readInt()
            // Validate before allocating so a hostile length prefix cannot force a huge buffer.
            if (length < 0 || length > MAX_PAYLOAD_BYTES) {
                throw IOException("Peer sent a cast frame of $length bytes (max $MAX_PAYLOAD_BYTES).")
            }
            val payload = ByteArray(length)
            input.readFully(payload)
            return CastFrame(type, timestampUs, payload)
        }
    }
}
