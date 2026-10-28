package com.mesh.core

import kotlin.math.roundToInt

/** What the encoder is set to for one point on the quality scale. */
data class CastProfile(
    val width: Int,
    val height: Int,
    val frameRate: Int,
    val bitrate: Int,
    val keyframeIntervalSeconds: Int
)

/**
 * The one control the user has over the picture, turned into encoder settings.
 *
 * 0 is the smooth end: every frame the screen produces, scaled down so the bitrate goes into
 * keeping up with motion. 100 is the sharp end: the screen's own resolution and few frames, so
 * the bitrate goes into detail — small text and sheet music, which is what this scale exists
 * for. Everything in between moves both at once.
 */
object CastQuality {
    /** Beyond this a LAN starts to matter more than the encoder, and no phone screen needs it. */
    const val MAX_BITRATE = 20_000_000

    /** Below this even a still page looks mushy after a scene change. */
    const val MIN_BITRATE = 1_000_000

    private const val SMOOTH_FRAME_RATE = 60
    private const val SHARP_FRAME_RATE = 10

    /** The long side the smooth end scales down to: a 4K phone screen at 60 fps is bitrate spent
     * on pixels nobody is reading. */
    private const val SMOOTH_LONG_SIDE = 1920

    /** Bits per pixel per frame, at the smooth and the sharp end. Sharp spends four times as
     * much on each pixel, which is where readable text comes from. */
    private const val SMOOTH_BITS_PER_PIXEL = 0.05
    private const val SHARP_BITS_PER_PIXEL = 0.20

    fun profileFor(quality: Int, screenWidth: Int, screenHeight: Int): CastProfile {
        val q = quality.coerceIn(CastSettings.MIN_QUALITY, CastSettings.MAX_QUALITY) / 100.0

        val frameRate = (SMOOTH_FRAME_RATE - (SMOOTH_FRAME_RATE - SHARP_FRAME_RATE) * q).roundToInt()

        val nativeLongSide = maxOf(screenWidth, screenHeight)
        // Never scale up: a screen already smaller than the cap stays as it is at every point on
        // the scale.
        val longSide = if (nativeLongSide <= SMOOTH_LONG_SIDE) nativeLongSide
        else (SMOOTH_LONG_SIDE + (nativeLongSide - SMOOTH_LONG_SIDE) * q).roundToInt()
        val scale = longSide.toDouble() / nativeLongSide
        val width = even((screenWidth * scale).roundToInt())
        val height = even((screenHeight * scale).roundToInt())

        val bitsPerPixel = SMOOTH_BITS_PER_PIXEL + (SHARP_BITS_PER_PIXEL - SMOOTH_BITS_PER_PIXEL) * q
        val bitrate = (width.toLong() * height * frameRate * bitsPerPixel).toLong()
            .coerceIn(MIN_BITRATE.toLong(), MAX_BITRATE.toLong()).toInt()

        // A still page settles faster when keyframes come often; motion does not need them.
        val keyframeIntervalSeconds = if (q >= 0.5) 1 else 2

        return CastProfile(width, height, frameRate, bitrate, keyframeIntervalSeconds)
    }

    /** H.264 wants even dimensions; some encoders reject an odd one outright. */
    private fun even(value: Int): Int = if (value % 2 == 0) value else value - 1
}
