package com.mesh.core

import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.os.Bundle
import android.view.Surface
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * The sending half of a cast: the screen, through a virtual display, into a hardware H.264
 * encoder, out as [CastFrame]s on the media plane.
 *
 * What it captures is what the phone is showing — Android gives a normal app no way to put one
 * app on a far screen and leave the phone free (see the design document). The quality scale is
 * the only control over the picture, and it arrives here as a [CastProfile].
 */
class ScreenCaster(
    private val projection: MediaProjection,
    private val channel: CastChannel,
    private val scope: CoroutineScope,
    private val screenDpi: Int
) {
    /** Called once when the cast ends by itself: the system revoked the capture, the encoder
     * failed, or the media plane died. Never called by [stop]. */
    var onEnded: ((String) -> Unit)? = null

    private var codec: MediaCodec? = null
    private var inputSurface: Surface? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var pump: Job? = null
    private var profile: CastProfile? = null
    private var stopped = false
    private val audio = AudioCaster(projection, channel, scope)

    private val projectionCallback = object : MediaProjection.Callback() {
        override fun onStop() {
            if (!stopped) end("The phone stopped sharing its screen.")
        }
    }

    fun start(profile: CastProfile) {
        projection.registerCallback(projectionCallback, null)
        startEncoder(profile)
    }

    /**
     * Applies a new point on the quality scale. A bitrate-only change is handed to the running
     * encoder; a different size or frame rate restarts it, which re-sends the codec
     * configuration the receiver needs before it can decode anything.
     */
    fun applyProfile(next: CastProfile) {
        val current = profile
        if (current == next) return
        if (current != null &&
            current.width == next.width &&
            current.height == next.height &&
            current.frameRate == next.frameRate
        ) {
            profile = next
            runCatching {
                codec?.setParameters(Bundle().apply {
                    putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, next.bitrate)
                })
            }
            return
        }
        restart(next)
    }

    /** The screen rotated or otherwise changed size: the encoder has to be rebuilt around the
     * new dimensions, exactly as a resolution change from the quality scale is. */
    fun applyScreenSize(quality: Int, screenWidth: Int, screenHeight: Int) {
        restart(CastQuality.profileFor(quality, screenWidth, screenHeight))
    }

    /** Switches the phone's sound on or off while the cast runs. Only apps that allow being
     * captured are heard — see [AudioCaster]. */
    fun setAudioEnabled(enabled: Boolean) {
        if (!audio.isSupported) return
        if (enabled) {
            runCatching { audio.start() }.onFailure {
                end("The phone could not capture its audio: ${it.message ?: it.javaClass.simpleName}")
            }
        } else {
            audio.stop()
        }
    }

    fun stop() {
        stopped = true
        audio.stop()
        releaseEncoder()
        runCatching { projection.unregisterCallback(projectionCallback) }
        runCatching { projection.stop() }
    }

    private fun restart(next: CastProfile) {
        releaseEncoder()
        if (!stopped) startEncoder(next)
    }

    private fun startEncoder(next: CastProfile) {
        profile = next
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, next.width, next.height).apply {
                setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                setInteger(MediaFormat.KEY_BIT_RATE, next.bitrate)
                setInteger(MediaFormat.KEY_FRAME_RATE, next.frameRate)
                setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, next.keyframeIntervalSeconds)
            }
            val encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            val surface = encoder.createInputSurface()
            encoder.start()

            codec = encoder
            inputSurface = surface
            virtualDisplay = projection.createVirtualDisplay(
                "mesh-cast",
                next.width,
                next.height,
                screenDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                surface,
                null,
                null)
            pump = scope.launch(Dispatchers.IO) { pumpFrames(encoder) }
        } catch (e: Exception) {
            releaseEncoder()
            end("The phone could not start encoding: ${e.message ?: e.javaClass.simpleName}")
        }
    }

    private suspend fun pumpFrames(encoder: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        while (scope.isActive && !stopped) {
            val index = try {
                encoder.dequeueOutputBuffer(info, 100_000)
            } catch (e: IllegalStateException) {
                return // the encoder was released under us by a restart
            }
            if (index < 0) continue
            val buffer = encoder.getOutputBuffer(index)
            if (buffer == null) {
                encoder.releaseOutputBuffer(index, false)
                continue
            }
            val payload = ByteArray(info.size)
            buffer.position(info.offset)
            buffer.get(payload)
            encoder.releaseOutputBuffer(index, false)

            val type = when {
                info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0 -> CastFrameType.VIDEO_CONFIG
                info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0 -> CastFrameType.VIDEO_KEY
                else -> CastFrameType.VIDEO_DELTA
            }
            try {
                channel.writeFrame(CastFrame(type, info.presentationTimeUs, payload))
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                end("The connection to the other device dropped.")
                return
            }
        }
    }

    private fun releaseEncoder() {
        pump?.cancel()
        pump = null
        runCatching { virtualDisplay?.release() }
        virtualDisplay = null
        runCatching { codec?.stop() }
        runCatching { codec?.release() }
        codec = null
        runCatching { inputSurface?.release() }
        inputSurface = null
    }

    private fun end(reason: String) {
        if (stopped) return
        stopped = true
        audio.stop()
        releaseEncoder()
        runCatching { projection.unregisterCallback(projectionCallback) }
        runCatching { projection.stop() }
        onEnded?.invoke(reason)
    }
}
