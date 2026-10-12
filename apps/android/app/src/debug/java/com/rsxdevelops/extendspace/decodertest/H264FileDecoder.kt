package com.rsxdevelops.extendspace.decodertest

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import com.rsxdevelops.extendspace.decodertest.h264.AccessUnit
import com.rsxdevelops.extendspace.decodertest.h264.AccessUnitReader
import com.rsxdevelops.extendspace.decodertest.h264.MalformedStreamException
import com.rsxdevelops.extendspace.decodertest.h264.NalType
import com.rsxdevelops.extendspace.decodertest.h264.SequenceParameterSet
import java.io.InputStream
import java.nio.ByteBuffer
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.math.roundToInt

/** A `.h264` file that can be opened more than once: first to read its parameters, then to decode it. */
internal class SampleSource(
    val displayName: String,
    val sizeBytes: Long?,
    val open: () -> InputStream,
)

internal data class DecodeOptions(
    /** A decoder to use by name; null lets the platform choose, as an app normally would. */
    val decoderName: String? = null,
    /** Show frames at the stream's own rate. Off decodes and shows them as fast as the device will. */
    val paced: Boolean = true,
    /** Ask the decoder for low-latency mode (`KEY_LOW_LATENCY`, Android 11 and later). */
    val lowLatency: Boolean = false,
)

internal enum class DecodeState { Idle, Preparing, Decoding, Completed, Stopped, Failed }

/** Everything the validation screen shows. Counts and times are measured; nothing is estimated. */
internal data class DecodeStatus(
    val state: DecodeState = DecodeState.Idle,
    val sampleName: String? = null,
    val sampleBytes: Long? = null,
    val options: DecodeOptions = DecodeOptions(),
    val stream: SequenceParameterSet? = null,
    val decoderName: String? = null,
    val decoderKind: String? = null,
    val decoderChosenBy: String? = null,
    val capabilityCheck: String? = null,
    val lowLatency: String? = null,
    val outputFormat: String? = null,
    val accessUnitsQueued: Long = 0,
    val keyFramesQueued: Long = 0,
    val bytesQueued: Long = 0,
    val framesDecoded: Long = 0,
    /** Frames the system reported as shown. Android documents this callback as informational. */
    val framesReportedDisplayed: Long = 0,
    /** From starting the decoder to releasing the most recent frame to the screen. */
    val elapsedNanos: Long = 0,
    val timeToFirstFrameNanos: Long? = null,
    val error: String? = null,
) {
    val isRunning: Boolean get() = state == DecodeState.Preparing || state == DecodeState.Decoding

    /** Frames decoded per second of elapsed time, or null before there is anything to divide. */
    val framesPerSecond: Double?
        get() = if (framesDecoded > 0 && elapsedNanos > 0) framesDecoded * 1e9 / elapsedNanos else null
}

/**
 * Decodes one `.h264` file with `MediaCodec` onto a [Surface], once.
 *
 * The decoder runs in asynchronous mode on its own thread and pulls the file through: each time it
 * has a free input buffer it is given the next access unit, and each decoded frame is released to
 * the surface. Nothing is queued in between, so memory use does not depend on the file's length.
 *
 * [start] and [stop] are called from the UI thread; [onStatus] is called on the decoder's thread.
 */
internal class H264FileDecoder(
    private val source: SampleSource,
    private val surface: Surface,
    private val options: DecodeOptions,
    private val onStatus: (DecodeStatus) -> Unit,
) {
    private val thread = HandlerThread("h264-file-decoder")
    private lateinit var handler: Handler

    // Everything below is touched only on the decoder thread.
    private var codec: MediaCodec? = null
    private var reader: AccessUnitReader? = null
    private var status = DecodeStatus(
        state = DecodeState.Preparing,
        sampleName = source.displayName,
        sampleBytes = source.sizeBytes,
        options = options,
    )
    private var finished = false
    private var inputDone = false
    private var frameDurationMicros = (MICROS_PER_SECOND / FALLBACK_FRAMES_PER_SECOND).toLong()
    private var startNanos = 0L
    private var playbackOriginNanos = 0L
    private var lastPublishNanos = 0L

    fun start() {
        thread.start()
        handler = Handler(thread.looper)
        handler.post(::prepare)
    }

    /**
     * Stops decoding and releases the decoder, returning once that is done. The surface must not be
     * destroyed while a decoder still renders to it, so callers rely on this being synchronous.
     */
    fun stop() {
        if (!thread.isAlive) return

        val released = CountDownLatch(1)
        val posted = handler.postAtFrontOfQueue {
            finish(DecodeState.Stopped, error = null)
            released.countDown()
        }
        if (posted) released.await(STOP_TIMEOUT_SECONDS, TimeUnit.SECONDS)
        thread.join(TimeUnit.SECONDS.toMillis(STOP_TIMEOUT_SECONDS))
    }

    private fun prepare() {
        if (finished) return
        publish(force = true)

        try {
            val parameters = readParameterSets()
            val sps = parameters.sps
            sps.framesPerSecond?.let { frameDurationMicros = (MICROS_PER_SECOND / it).toLong().coerceAtLeast(1) }
            status = status.copy(stream = sps)

            val decoder = chooseDecoder(sps)
            val check = DecoderCatalog.check(decoder, sps)
            status = status.copy(
                decoderName = decoder.name,
                decoderKind = DecoderCatalog.kind(decoder) + if (decoder.isVendor) ", vendor" else ", platform",
                decoderChosenBy = if (options.decoderName == null) "the platform (findDecoderForFormat)" else "you",
                capabilityCheck = check.describe(),
            )

            val format = MediaFormat.createVideoFormat(DecoderCatalog.MIME_TYPE, sps.width, sps.height).apply {
                // The decoder is configured before any picture arrives. The same parameter sets are
                // still in the stream, ahead of each keyframe, and are fed as part of those pictures.
                setByteBuffer("csd-0", withStartCode(parameters.spsNal))
                setByteBuffer("csd-1", withStartCode(parameters.ppsNal))
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxInputSize(sps))
                sps.framesPerSecond?.let { setInteger(MediaFormat.KEY_FRAME_RATE, it.roundToInt()) }
            }
            status = status.copy(lowLatency = requestLowLatency(decoder, format))

            val codec = MediaCodec.createByCodecName(decoder.name)
            this.codec = codec
            codec.setCallback(callback, handler)
            codec.setOnFrameRenderedListener(
                { _, _, _ -> status = status.copy(framesReportedDisplayed = status.framesReportedDisplayed + 1) },
                handler,
            )
            codec.configure(format, surface, null, 0)

            reader = AccessUnitReader(source.open())
            status = status.copy(state = DecodeState.Decoding)
            startNanos = System.nanoTime()
            codec.start()
            publish(force = true)
        } catch (exception: Exception) {
            finish(DecodeState.Failed, describe(exception))
        }
    }

    private class ParameterSets(val sps: SequenceParameterSet, val spsNal: ByteArray, val ppsNal: ByteArray)

    /** Reads far enough into the file to find the first SPS and PPS, which the first picture carries. */
    private fun readParameterSets(): ParameterSets {
        AccessUnitReader(source.open()).use { probe ->
            var spsNal: ByteArray? = null
            var ppsNal: ByteArray? = null
            repeat(PARAMETER_SET_SEARCH_ACCESS_UNITS) {
                val unit: AccessUnit = probe.next() ?: return@repeat
                spsNal = spsNal ?: unit.firstOfType(NalType.SPS)
                ppsNal = ppsNal ?: unit.firstOfType(NalType.PPS)
                val sps = spsNal
                val pps = ppsNal
                if (sps != null && pps != null) {
                    return ParameterSets(SequenceParameterSet.parse(sps), sps, pps)
                }
            }

            throw MalformedStreamException(
                when {
                    probe.accessUnitsRead == 0L -> "The file contains no H.264 pictures."
                    spsNal == null -> "No sequence parameter set (SPS) in the first ${probe.accessUnitsRead} pictures; the decoder cannot be configured."
                    else -> "No picture parameter set (PPS) in the first ${probe.accessUnitsRead} pictures; the decoder cannot be configured."
                },
            )
        }
    }

    private fun chooseDecoder(sps: SequenceParameterSet): MediaCodecInfo {
        val decoders = DecoderCatalog.h264Decoders()
        if (decoders.isEmpty()) throw UnsupportedStreamException("This device reports no H.264 decoder at all.")

        val name = options.decoderName
            ?: DecoderCatalog.platformChoice(sps.width, sps.height)
            ?: throw UnsupportedStreamException(
                "No H.264 decoder on this device reports support for ${sps.width} x ${sps.height}. " +
                    "Capture at a smaller --output-size, or pick a decoder by hand to try anyway.",
            )

        return decoders.firstOrNull { it.name == name }
            ?: throw UnsupportedStreamException("This device has no H.264 decoder named $name.")
    }

    /** Applies the low-latency request if asked for, and says what happened to it. */
    private fun requestLowLatency(decoder: MediaCodecInfo, format: MediaFormat): String {
        if (!options.lowLatency) return "not requested"
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return "requested, but this Android version has no low-latency key"

        format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        val reported = decoder.getCapabilitiesForType(DecoderCatalog.MIME_TYPE)
            .isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)
        return if (reported) {
            "requested; the decoder reports the feature"
        } else {
            "requested; the decoder does not report the feature, so it may ignore the request"
        }
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
            if (finished || inputDone) return
            try {
                val unit = reader?.next()
                val timeMicros = status.accessUnitsQueued * frameDurationMicros
                if (unit == null) {
                    inputDone = true
                    codec.queueInputBuffer(index, 0, 0, timeMicros, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                    return
                }

                val buffer = codec.getInputBuffer(index)
                    ?: throw IllegalStateException("The decoder offered input buffer $index and then withheld it.")
                if (unit.data.size > buffer.capacity()) {
                    throw UnsupportedStreamException(
                        "Picture ${status.accessUnitsQueued + 1} is ${unit.data.size} bytes, more than the decoder's input buffer of ${buffer.capacity()} bytes.",
                    )
                }

                buffer.clear()
                buffer.put(unit.data)
                val keyFrame = unit.isIdr
                codec.queueInputBuffer(index, 0, unit.data.size, timeMicros, if (keyFrame) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
                status = status.copy(
                    accessUnitsQueued = status.accessUnitsQueued + 1,
                    keyFramesQueued = status.keyFramesQueued + if (keyFrame) 1 else 0,
                    bytesQueued = status.bytesQueued + unit.data.size,
                )
                publish()
            } catch (exception: Exception) {
                finish(DecodeState.Failed, describe(exception))
            }
        }

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            if (finished) return
            try {
                val endOfStream = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0
                if (info.size > 0) {
                    val arrival = System.nanoTime()
                    if (status.framesDecoded == 0L) {
                        status = status.copy(timeToFirstFrameNanos = arrival - startNanos)
                        playbackOriginNanos = arrival - info.presentationTimeUs * NANOS_PER_MICRO
                    }
                    if (options.paced) sleepUntil(playbackOriginNanos + info.presentationTimeUs * NANOS_PER_MICRO)

                    codec.releaseOutputBuffer(index, true)
                    status = status.copy(
                        framesDecoded = status.framesDecoded + 1,
                        elapsedNanos = System.nanoTime() - startNanos,
                    )
                } else {
                    codec.releaseOutputBuffer(index, false)
                }

                if (endOfStream) finish(DecodeState.Completed, error = null) else publish()
            } catch (exception: Exception) {
                finish(DecodeState.Failed, describe(exception))
            }
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
            if (finished) return
            status = status.copy(outputFormat = describeOutput(format))
            publish(force = true)
        }

        override fun onError(codec: MediaCodec, exception: MediaCodec.CodecException) {
            finish(DecodeState.Failed, describe(exception))
        }
    }

    /** Releases everything and reports the final state. Runs once; later calls do nothing. */
    private fun finish(state: DecodeState, error: String?) {
        if (finished) return
        finished = true

        var releaseError: String? = null
        codec?.let { codec ->
            try {
                // No stop() first: after a decoder error stop() itself throws, and release() alone
                // is documented to free the codec from any state.
                codec.release()
            } catch (exception: Exception) {
                releaseError = "Releasing the decoder failed: ${describe(exception)}"
            }
        }
        codec = null

        try {
            reader?.close()
        } catch (exception: Exception) {
            releaseError = releaseError ?: "Closing the file failed: ${describe(exception)}"
        }
        reader = null

        val problem = error ?: releaseError
        val incomplete = state == DecodeState.Completed && status.framesDecoded != status.accessUnitsQueued
        status = status.copy(
            state = if (problem != null) DecodeState.Failed else state,
            error = problem ?: if (incomplete) {
                "The decoder returned ${status.framesDecoded} frames for ${status.accessUnitsQueued} pictures."
            } else {
                null
            },
        )
        publish(force = true)
        thread.quitSafely()
    }

    private fun publish(force: Boolean = false) {
        val now = System.nanoTime()
        if (force || now - lastPublishNanos >= PUBLISH_INTERVAL_NANOS) {
            lastPublishNanos = now
            onStatus(status)
        }
    }

    private fun sleepUntil(deadlineNanos: Long) {
        val wait = deadlineNanos - System.nanoTime()
        if (wait > 0) {
            // Bounded, so that a stream with a wrong frame rate cannot park the decoder thread.
            val bounded = wait.coerceAtMost(MAX_PACING_WAIT_NANOS)
            Thread.sleep(bounded / NANOS_PER_MILLI, (bounded % NANOS_PER_MILLI).toInt())
        }
    }

    private fun describeOutput(format: MediaFormat): String = buildString {
        val width = format.getInteger(MediaFormat.KEY_WIDTH)
        val height = format.getInteger(MediaFormat.KEY_HEIGHT)
        append("$width x $height buffers")
        if (format.containsKey(KEY_CROP_RIGHT) && format.containsKey(KEY_CROP_BOTTOM)) {
            val cropWidth = format.getInteger(KEY_CROP_RIGHT) - format.getInteger(KEY_CROP_LEFT) + 1
            val cropHeight = format.getInteger(KEY_CROP_BOTTOM) - format.getInteger(KEY_CROP_TOP) + 1
            append(", $cropWidth x $cropHeight visible")
        }
        if (format.containsKey(MediaFormat.KEY_COLOR_FORMAT)) {
            append(", colour format 0x${format.getInteger(MediaFormat.KEY_COLOR_FORMAT).toString(16)}")
        }
        if (format.containsKey(MediaFormat.KEY_COLOR_STANDARD)) {
            append(", colour standard ${format.getInteger(MediaFormat.KEY_COLOR_STANDARD)}")
        }
        if (format.containsKey(MediaFormat.KEY_COLOR_RANGE)) {
            append(", colour range ${format.getInteger(MediaFormat.KEY_COLOR_RANGE)}")
        }
    }

    private fun describe(exception: Exception): String = when (exception) {
        is MalformedStreamException, is UnsupportedStreamException -> exception.message.orEmpty()
        is MediaCodec.CodecException ->
            "The decoder failed: ${exception.message} (${exception.diagnosticInfo}; " +
                "recoverable: ${exception.isRecoverable}, transient: ${exception.isTransient})"
        else -> "${exception.javaClass.simpleName}: ${exception.message}"
    }

    private class UnsupportedStreamException(message: String) : Exception(message)

    private companion object {
        const val MICROS_PER_SECOND = 1_000_000.0
        const val NANOS_PER_MICRO = 1_000L
        const val NANOS_PER_MILLI = 1_000_000L
        const val FALLBACK_FRAMES_PER_SECOND = 60
        const val PARAMETER_SET_SEARCH_ACCESS_UNITS = 32
        const val PUBLISH_INTERVAL_NANOS = 100 * NANOS_PER_MILLI
        const val MAX_PACING_WAIT_NANOS = 500 * NANOS_PER_MILLI
        const val STOP_TIMEOUT_SECONDS = 3L
        const val MIN_INPUT_BYTES = 512 * 1024

        // MediaFormat's crop keys were only made public constants in API 33; the strings are older.
        const val KEY_CROP_LEFT = "crop-left"
        const val KEY_CROP_TOP = "crop-top"
        const val KEY_CROP_RIGHT = "crop-right"
        const val KEY_CROP_BOTTOM = "crop-bottom"

        val START_CODE = byteArrayOf(0, 0, 0, 1)

        fun withStartCode(nal: ByteArray): ByteBuffer = ByteBuffer.wrap(START_CODE + nal)

        /**
         * H.264 limits a picture to half its uncompressed size at these levels, so three quarters
         * of a byte per pixel is room to spare without asking the decoder for huge buffers.
         */
        fun maxInputSize(sps: SequenceParameterSet): Int = maxOf(MIN_INPUT_BYTES, sps.width * sps.height * 3 / 4)
    }
}
