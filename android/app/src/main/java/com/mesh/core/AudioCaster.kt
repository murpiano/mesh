package com.mesh.core

import android.annotation.SuppressLint
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.os.Build
import androidx.annotation.RequiresApi
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * The sound half of a cast: what the phone is playing, captured through the same
 * MediaProjection as the picture and encoded as AAC onto the same media plane.
 *
 * Only apps that allow being captured are heard. Players that opt out, and most DRM playback,
 * come through silent — that is Android's rule for AudioPlaybackCapture, not something to work
 * around, so nothing here tries to detect or hide it.
 */
class AudioCaster(
    private val projection: MediaProjection,
    private val channel: CastChannel,
    private val scope: CoroutineScope
) {
    private var record: AudioRecord? = null
    private var codec: MediaCodec? = null
    private var pump: Job? = null

    val isRunning: Boolean get() = pump?.isActive == true

    /** True when this device can capture playback at all: the API arrived in Android 10. */
    val isSupported: Boolean get() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q

    @SuppressLint("MissingPermission") // RECORD_AUDIO is requested before this is ever called
    @RequiresApi(Build.VERSION_CODES.Q)
    fun start() {
        if (isRunning) return
        val format = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(SAMPLE_RATE)
            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
            .build()
        val capture = AudioPlaybackCaptureConfiguration.Builder(projection)
            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
            .addMatchingUsage(AudioAttributes.USAGE_GAME)
            .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
            .build()
        val bufferSize = maxOf(
            AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT),
            READ_SIZE_BYTES)

        val recorder = AudioRecord.Builder()
            .setAudioFormat(format)
            .setBufferSizeInBytes(bufferSize)
            .setAudioPlaybackCaptureConfig(capture)
            .build()

        val encoderFormat = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, SAMPLE_RATE, CHANNELS).apply {
            setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
            setInteger(MediaFormat.KEY_BIT_RATE, BITRATE)
        }
        val encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_AAC)
        encoder.configure(encoderFormat, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        encoder.start()
        recorder.startRecording()

        record = recorder
        codec = encoder
        pump = scope.launch(Dispatchers.IO) { pumpAudio(recorder, encoder) }
    }

    fun stop() {
        pump?.cancel()
        pump = null
        runCatching { record?.stop() }
        runCatching { record?.release() }
        record = null
        runCatching { codec?.stop() }
        runCatching { codec?.release() }
        codec = null
    }

    private suspend fun pumpAudio(recorder: AudioRecord, encoder: MediaCodec) {
        val pcm = ByteArray(READ_SIZE_BYTES)
        val info = MediaCodec.BufferInfo()
        val startedAt = System.nanoTime()
        while (scope.isActive) {
            val read = recorder.read(pcm, 0, pcm.size)
            if (read > 0) {
                val index = encoder.dequeueInputBuffer(TIMEOUT_US)
                if (index >= 0) {
                    val buffer = encoder.getInputBuffer(index)
                    buffer?.clear()
                    buffer?.put(pcm, 0, read)
                    val timestampUs = (System.nanoTime() - startedAt) / 1_000
                    encoder.queueInputBuffer(index, 0, read, timestampUs, 0)
                }
            }
            while (true) {
                val index = try {
                    encoder.dequeueOutputBuffer(info, 0)
                } catch (e: IllegalStateException) {
                    return // released under us by stop()
                }
                if (index < 0) break
                val buffer = encoder.getOutputBuffer(index)
                if (buffer == null) {
                    encoder.releaseOutputBuffer(index, false)
                    continue
                }
                val payload = ByteArray(info.size)
                buffer.position(info.offset)
                buffer.get(payload)
                encoder.releaseOutputBuffer(index, false)

                val type = if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                    CastFrameType.AUDIO_CONFIG
                } else {
                    CastFrameType.AUDIO_DATA
                }
                try {
                    channel.writeFrame(CastFrame(type, info.presentationTimeUs, payload))
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    return // the media plane died; the video pump reports it
                }
            }
        }
    }

    private companion object {
        const val SAMPLE_RATE = 44_100
        const val CHANNELS = 2
        const val BITRATE = 128_000
        const val READ_SIZE_BYTES = 4096
        const val TIMEOUT_US = 10_000L
    }
}
