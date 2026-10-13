package com.rsxdevelops.extendspace.livestream

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import com.rsxdevelops.extendspace.decodertest.DecoderCatalog
import com.rsxdevelops.extendspace.decodertest.h264.SequenceParameterSet
import java.nio.ByteBuffer
import java.util.ArrayDeque
import java.util.concurrent.TimeUnit
import kotlin.math.roundToInt

/** What the live session needs to hear from the decoder. All of it arrives on the decoder's own thread. */
internal interface LiveDecoderListener {
    /** A decoded frame was released to the surface. [timestampMicros] is the capture time it was queued with. */
    fun onFrameRendered(timestampMicros: Long, releasedNanos: Long)

    /**
     * The system reported a frame as rendered. Only the fact is passed on: the time that comes with
     * the callback was measured to be the timestamp the frame was queued with rather than when it
     * reached the screen, so it is not offered as a display time.
     */
    fun onFrameDisplayed(timestampMicros: Long)

    /** The decoder failed and is of no further use; it must be released and a new one made at the next keyframe. */
    fun onDecoderError(message: String)
}

/**
 * A `MediaCodec` H.264 decoder for pictures that arrive when they arrive, rather than being pulled
 * from a file. It runs in asynchronous mode: the codec announces each free input buffer, and a
 * picture is put into one as soon as both exist. Decoded frames are released to the surface at
 * once; nothing is held back for smoothness.
 *
 * Pictures waiting for an input buffer are limited to [MAX_PENDING], or [MAX_PENDING_AT_STARTUP]
 * until the first frame has come out. When the decoder is that far behind, [submit] refuses the
 * picture so that the caller can drop to the next keyframe instead of letting delay build up.
 *
 * [submit] and [release] are called by the connection's reader thread; the codec calls back on a
 * thread of this object's own.
 */
internal class LiveDecoder(
    surface: Surface,
    val parameters: SequenceParameterSet,
    spsNal: ByteArray,
    ppsNal: ByteArray,
    val decoderName: String,
    lowLatency: Boolean,
    private val listener: LiveDecoderListener,
) {
    private class Pending(val data: ByteArray, val timestampMicros: Long, val keyFrame: Boolean)

    private val thread = HandlerThread("live-h264-decoder").apply { start() }
    private val handler = Handler(thread.looper)
    private val lock = Any()
    private val freeInputs = ArrayDeque<Int>()
    private val pending = ArrayDeque<Pending>()
    private val codec: MediaCodec = MediaCodec.createByCodecName(decoderName)
    private var released = false

    @Volatile
    private var producedOutput = false

    /** The parameter sets this decoder was configured with, for telling when a stream has changed. */
    val configuredWith: ByteArray = spsNal + ppsNal

    /** What the decoder says it outputs, once it has said so. */
    @Volatile
    var outputFormat: String? = null
        private set

    /** What became of the low-latency request. */
    var lowLatency: String = "not requested"
        private set

    /** How many pictures are waiting for an input buffer right now. */
    val queueDepth: Int get() = synchronized(lock) { pending.size }

    /**
     * Gives the decoder one access unit: [length] bytes of [data] from [offset]. Returns false,
     * having taken nothing, when the decoder already has as many pictures waiting as it may or has
     * been released.
     *
     * @param timestampMicros the capture time; it comes back with the decoded frame.
     */
    fun submit(data: ByteArray, offset: Int, length: Int, timestampMicros: Long, keyFrame: Boolean): Boolean {
        val index: Int
        synchronized(lock) {
            if (released) return false
            val free = freeInputs.poll()
            if (free == null) {
                if (pending.size >= if (producedOutput) MAX_PENDING else MAX_PENDING_AT_STARTUP) return false
                pending.add(Pending(data.copyOfRange(offset, offset + length), timestampMicros, keyFrame))
                return true
            }
            index = free
        }

        // Straight from the receive buffer into the codec's: one copy.
        return queue(index, data, offset, length, timestampMicros, keyFrame)
    }

    /** Stops the codec and its thread. Safe to call more than once and from any thread but the codec's own. */
    fun release() {
        synchronized(lock) {
            if (released) return
            released = true
            pending.clear()
            freeInputs.clear()
        }

        try {
            codec.release()
        } catch (exception: IllegalStateException) {
            // Already released by the framework after an error; there is nothing left to free.
        }
        thread.quitSafely()
        thread.join(TimeUnit.SECONDS.toMillis(2))
    }

    private fun queue(index: Int, data: ByteArray, offset: Int, length: Int, timestampMicros: Long, keyFrame: Boolean): Boolean {
        try {
            val buffer = codec.getInputBuffer(index) ?: throw IllegalStateException("The decoder withheld input buffer $index.")
            if (length > buffer.capacity()) {
                throw IllegalStateException("A picture of $length bytes does not fit the decoder's ${buffer.capacity()}-byte input buffer.")
            }
            buffer.clear()
            buffer.put(data, offset, length)
            codec.queueInputBuffer(index, 0, length, timestampMicros, if (keyFrame) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
            return true
        } catch (exception: IllegalStateException) {
            // Includes MediaCodec.CodecException. Released concurrently is not an error worth reporting.
            if (!synchronized(lock) { released }) listener.onDecoderError(describe(exception))
            return false
        }
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
            val next: Pending
            synchronized(lock) {
                if (released) return
                val waiting = pending.poll()
                if (waiting == null) {
                    freeInputs.add(index)
                    return
                }
                next = waiting
            }
            queue(index, next.data, 0, next.data.size, next.timestampMicros, next.keyFrame)
        }

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            if (synchronized(lock) { released }) return
            try {
                if (info.size > 0) {
                    producedOutput = true
                    // "Show this now": the release time is the display time asked for. The frame's
                    // own timestamp is on the host's clock and would mean nothing to the compositor.
                    val releasedNanos = System.nanoTime()
                    codec.releaseOutputBuffer(index, releasedNanos)
                    listener.onFrameRendered(info.presentationTimeUs, releasedNanos)
                } else {
                    codec.releaseOutputBuffer(index, false)
                }
            } catch (exception: IllegalStateException) {
                if (!synchronized(lock) { released }) listener.onDecoderError(describe(exception))
            }
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {
            outputFormat = "${format.getInteger(MediaFormat.KEY_WIDTH)} x ${format.getInteger(MediaFormat.KEY_HEIGHT)} buffers"
        }

        override fun onError(codec: MediaCodec, exception: MediaCodec.CodecException) {
            if (!synchronized(lock) { released }) listener.onDecoderError(describe(exception))
        }
    }

    init {
        try {
            val format = MediaFormat.createVideoFormat(DecoderCatalog.MIME_TYPE, parameters.width, parameters.height).apply {
                setByteBuffer("csd-0", ByteBuffer.wrap(START_CODE + spsNal))
                setByteBuffer("csd-1", ByteBuffer.wrap(START_CODE + ppsNal))
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(MIN_INPUT_BYTES, parameters.width * parameters.height * 3 / 4))
                parameters.framesPerSecond?.let { setInteger(MediaFormat.KEY_FRAME_RATE, it.roundToInt()) }
            }
            this.lowLatency = requestLowLatency(format, lowLatency)

            codec.setCallback(callback, handler)
            codec.setOnFrameRenderedListener(
                { _, presentationTimeUs, _ -> listener.onFrameDisplayed(presentationTimeUs) },
                handler,
            )
            codec.configure(format, surface, null, 0)
            codec.start()
        } catch (exception: Exception) {
            release()
            throw exception
        }
    }

    private fun requestLowLatency(format: MediaFormat, requested: Boolean): String {
        if (!requested) return "not requested"
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return "requested, but this Android version has no low-latency key"

        format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        val reported = DecoderCatalog.h264Decoders()
            .firstOrNull { it.name == decoderName }
            ?.getCapabilitiesForType(DecoderCatalog.MIME_TYPE)
            ?.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency) == true
        return if (reported) "requested; the decoder reports the feature" else "requested; the decoder does not report the feature"
    }

    private fun describe(exception: Exception): String = when (exception) {
        is MediaCodec.CodecException -> "The decoder failed: ${exception.message} (${exception.diagnosticInfo})"
        else -> "The decoder failed: ${exception.message}"
    }

    companion object {
        /** A few pictures of slack; at 60 fps this is 67 ms of delay at most before frames are dropped instead. */
        const val MAX_PENDING = 4

        /**
         * Starting a decoder takes on the order of a hundred milliseconds, during which the host
         * keeps sending. Those first pictures are kept rather than dropped, since dropping them
         * costs a keyframe; the decoder works through them far faster than they arrive. Measured:
         * with the ordinary limit, a 1080p60 stream lost its first 17 frames this way.
         */
        const val MAX_PENDING_AT_STARTUP = 30
        private const val MIN_INPUT_BYTES = 512 * 1024
        private val START_CODE = byteArrayOf(0, 0, 0, 1)
    }
}
