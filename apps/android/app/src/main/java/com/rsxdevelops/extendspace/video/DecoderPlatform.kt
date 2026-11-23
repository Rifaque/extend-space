package com.rsxdevelops.extendspace.video

import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.view.Surface
import com.rsxdevelops.extendspace.connection.DecoderCapability
import com.rsxdevelops.extendspace.connection.DecoderChoice
import com.rsxdevelops.extendspace.connection.VideoOutput
import com.rsxdevelops.extendspace.video.h264.SequenceParameterSet

/** A [VideoOutput] that is a `Surface`: what the display screen lends while its `SurfaceView` exists. */
class SurfaceOutput(val surface: Surface) : VideoOutput

/** One decoder bound to one output, from the receiver's side. The real one is [SurfaceDecoder]. */
internal interface FrameDecoder {
    /** The SPS and PPS it was configured with, to tell when a keyframe brings different ones. */
    val configuredWith: ByteArray

    /** Pictures waiting for an input buffer. */
    val queueDepth: Int

    /** What became of the low-latency request, for diagnostics. */
    val lowLatency: String

    /**
     * False, having taken nothing, when it is too far behind or has been released. A [keyFrame]
     * (an IDR picture) is taken rather than refused when the decoder is behind: the pictures still
     * waiting for it are dropped instead (D61), and [takeSuperseded] says how many.
     */
    fun submit(data: ByteArray, offset: Int, length: Int, timestampMicros: Long, keyFrame: Boolean): Boolean

    /** Pictures it had taken and then dropped, unseen by the codec, because a keyframe superseded them; since the last call. */
    fun takeSuperseded(): Int = 0

    /** Stops it and its thread; bounded. Safe more than once and from any thread but its own. */
    fun release()
}

/**
 * What the receiver needs from the device's codecs: what to offer the host, which decoder to use,
 * and a decoder bound to an output. The seam that lets the receiver's policy be tested on a desktop
 * JVM, where `MediaCodec` does not exist.
 */
internal interface DecoderPlatform {
    fun decoders(): List<DecoderCapability>

    /** The decoder the platform would choose for this size, or null if none claims it. */
    fun choose(width: Int, height: Int): DecoderChoice?

    /** Creates and starts a decoder drawing to [output]. Throws if it cannot. */
    fun create(
        output: VideoOutput,
        parameters: SequenceParameterSet,
        spsNal: ByteArray,
        ppsNal: ByteArray,
        decoder: DecoderChoice,
        listener: FrameDecoderListener,
    ): FrameDecoder
}

/** The real platform: `MediaCodecList` and [SurfaceDecoder]. */
internal class MediaCodecPlatform(private val lowLatency: Boolean = true) : DecoderPlatform {
    override fun decoders(): List<DecoderCapability> = H264Decoders.h264Decoders().map { info ->
        val capabilities = info.getCapabilitiesForType(H264Decoders.MIME_TYPE)
        val video = checkNotNull(capabilities.videoCapabilities) { "An H.264 decoder reported no video capabilities." }
        DecoderCapability(
            name = info.name,
            hardware = info.isHardwareAccelerated,
            lowLatency = capabilities.isFeatureSupported(H264Decoders.FEATURE_LOW_LATENCY),
            maxWidth = video.supportedWidths.upper,
            maxHeight = video.supportedHeights.upper,
        )
    }

    override fun choose(width: Int, height: Int): DecoderChoice? {
        val name = H264Decoders.platformChoice(width, height) ?: return null
        val info = H264Decoders.h264Decoders().firstOrNull { it.name == name } ?: return null
        return DecoderChoice(name, info.isHardwareAccelerated)
    }

    override fun create(
        output: VideoOutput,
        parameters: SequenceParameterSet,
        spsNal: ByteArray,
        ppsNal: ByteArray,
        decoder: DecoderChoice,
        listener: FrameDecoderListener,
    ): FrameDecoder {
        val surface = (output as? SurfaceOutput)?.surface ?: throw IllegalArgumentException("Video can be decoded only onto a SurfaceOutput.")
        return SurfaceDecoder(surface, parameters, spsNal, ppsNal, decoder.name, lowLatency, listener)
    }
}

/**
 * The device's H.264 decoders as `MediaCodecList` reports them. The part of the debug experiment's
 * `DecoderCatalog` that the production receiver uses; the report and the capability checks stay in
 * the debug build.
 */
internal object H264Decoders {
    const val MIME_TYPE = MediaFormat.MIMETYPE_VIDEO_AVC

    // CodecCapabilities.FEATURE_LowLatency, a named constant only from API 30.
    const val FEATURE_LOW_LATENCY = "low-latency"

    /** Every H.264 decoder, in the order the platform lists (and prefers) them. */
    fun h264Decoders(): List<MediaCodecInfo> =
        MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
            !info.isEncoder && info.supportedTypes.any { it.equals(MIME_TYPE, ignoreCase = true) }
        }

    /** The decoder the platform itself would choose for a stream of this size, or null if none claims it. */
    fun platformChoice(width: Int, height: Int): String? =
        MediaCodecList(MediaCodecList.REGULAR_CODECS)
            .findDecoderForFormat(MediaFormat.createVideoFormat(MIME_TYPE, width, height))
}
