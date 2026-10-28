package com.mesh.core

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException

/** Where the receiving device puts the cast window. */
enum class CastPlacementMode(val tag: Int) {
    /** An ordinary window, at [CastPlacement.x]/[CastPlacement.y]. */
    WINDOW(1),

    /** Filling the monitor named by [CastPlacement.monitorId]. */
    FULLSCREEN(2),

    /** Spanning every monitor the receiver has. */
    ALL_MONITORS(3);

    companion object {
        fun fromTag(tag: Int): CastPlacementMode =
            entries.firstOrNull { it.tag == tag } ?: throw IOException("Peer sent an unknown placement mode: $tag.")
    }
}

/** One monitor of the receiving device, as this device shows it in its own controls. The
 * receiver is the authority on this list — it sends it, and it corrects a placement that names
 * a monitor that has since gone away. */
data class CastMonitor(
    val id: String,
    val name: String,
    val width: Int,
    val height: Int,
    val isPrimary: Boolean
)

/** Where the window sits. [x]/[y]/[width]/[height] only mean anything in
 * [CastPlacementMode.WINDOW]; the other modes carry the last known rectangle so going back to a
 * window restores it. */
data class CastPlacement(
    val mode: CastPlacementMode,
    val monitorId: String,
    val x: Int,
    val y: Int,
    val width: Int,
    val height: Int
)

/**
 * Everything about a running cast that either device can change: how the picture is encoded,
 * whether audio comes with it, and where the receiving device shows it. The whole record is
 * replicated on every change from whichever side made it (last write wins) — it is small and
 * changes only when a human moves a control, so there is nothing to merge.
 *
 * MUST stay wire-compatible with src/windows/Mesh.Core/CastSettings.cs.
 */
data class CastSettings(
    val quality: Int,
    val audio: Boolean,
    val placement: CastPlacement,
    val monitors: List<CastMonitor>
) {
    companion object {
        /** 0 is the smooth end (high frame rate, scaled down), 100 the sharp end (native
         * resolution, few frames). Clamped on read so a peer can never push it out of range. */
        const val MIN_QUALITY = 0
        const val MAX_QUALITY = 100

        /** More monitors than any desk has. A longer list is a protocol error, not a
         * truncation, so the two sides never disagree about what was sent. */
        const val MAX_MONITORS = 16

        fun write(output: DataOutputStream, settings: CastSettings) {
            require(settings.monitors.size <= MAX_MONITORS) {
                "Cast settings carry ${settings.monitors.size} monitors (max $MAX_MONITORS)."
            }

            PairingProtocol.writeByte(output, settings.quality.coerceIn(MIN_QUALITY, MAX_QUALITY))
            PairingProtocol.writeByte(output, if (settings.audio) PairingProtocol.YES else PairingProtocol.NO)

            PairingProtocol.writeByte(output, settings.placement.mode.tag)
            PairingProtocol.writeString(output, settings.placement.monitorId)
            output.writeInt(settings.placement.x) // DataOutputStream.writeInt is big-endian
            output.writeInt(settings.placement.y)
            output.writeInt(settings.placement.width)
            output.writeInt(settings.placement.height)

            PairingProtocol.writeByte(output, settings.monitors.size)
            for (monitor in settings.monitors) {
                PairingProtocol.writeString(output, monitor.id)
                PairingProtocol.writeString(output, monitor.name)
                output.writeInt(monitor.width)
                output.writeInt(monitor.height)
                PairingProtocol.writeByte(output, if (monitor.isPrimary) PairingProtocol.YES else PairingProtocol.NO)
            }
        }

        fun read(input: DataInputStream): CastSettings {
            val quality = PairingProtocol.readByte(input).coerceIn(MIN_QUALITY, MAX_QUALITY)
            val audio = PairingProtocol.readByte(input) == PairingProtocol.YES

            val placement = CastPlacement(
                CastPlacementMode.fromTag(PairingProtocol.readByte(input)),
                PairingProtocol.readString(input),
                input.readInt(),
                input.readInt(),
                input.readInt(),
                input.readInt()
            )

            val count = PairingProtocol.readByte(input)
            if (count > MAX_MONITORS) throw IOException("Peer sent $count monitors (max $MAX_MONITORS).")
            val monitors = (0 until count).map {
                CastMonitor(
                    PairingProtocol.readString(input),
                    PairingProtocol.readString(input),
                    input.readInt(),
                    input.readInt(),
                    PairingProtocol.readByte(input) == PairingProtocol.YES
                )
            }

            return CastSettings(quality, audio, placement, monitors)
        }
    }
}
