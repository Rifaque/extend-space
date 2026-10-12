package com.rsxdevelops.extendspace.decodertest

import android.media.MediaCodecInfo
import android.media.MediaCodecInfo.CodecCapabilities
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import com.rsxdevelops.extendspace.decodertest.h264.AvcProfileLevel
import com.rsxdevelops.extendspace.decodertest.h264.SequenceParameterSet
import kotlin.math.roundToInt

/** How one decoder's reported capabilities compare with one stream. Nothing here is a test result. */
internal data class CapabilityCheck(
    val sizeSupported: Boolean,
    val sizeAndRateSupported: Boolean?,
    val profileAndLevelCovered: Boolean,
) {
    val allReported: Boolean get() = sizeSupported && sizeAndRateSupported != false && profileAndLevelCovered

    fun describe(): String = buildString {
        append("size ").append(if (sizeSupported) "within" else "OUTSIDE").append(" reported limits")
        when (sizeAndRateSupported) {
            true -> append(", size at the stream's frame rate within limits")
            false -> append(", size at the stream's frame rate OUTSIDE limits")
            null -> append(", frame rate not stated by the stream")
        }
        append(", profile and level ").append(if (profileAndLevelCovered) "covered" else "NOT covered")
    }
}

/**
 * What Android's codec list reports about this device's H.264 decoders. These are the device's
 * own claims; whether a stream really decodes is found out by decoding it.
 */
internal object DecoderCatalog {
    const val MIME_TYPE = MediaFormat.MIMETYPE_VIDEO_AVC

    /** Sizes worth knowing about for a second screen, checked against each decoder's limits. */
    private val CANDIDATE_SIZES = listOf(
        1280 to 720,
        1920 to 1080,
        1920 to 1200,
        2560 to 1440,
        2560 to 1600,
        3840 to 2160,
    )
    private val CANDIDATE_RATES = listOf(30, 60, 120)

    // CodecCapabilities.FEATURE_LowLatency. The platform only names it from API 30; asking an
    // older device about a feature it has never heard of simply answers "no".
    private const val FEATURE_LOW_LATENCY = "low-latency"

    /** Every H.264 decoder, in the order the platform lists (and prefers) them. */
    fun h264Decoders(): List<MediaCodecInfo> =
        MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
            !info.isEncoder && info.supportedTypes.any { it.equals(MIME_TYPE, ignoreCase = true) }
        }

    /** The decoder the platform itself would choose for a stream of this size, or null if none claims it. */
    fun platformChoice(width: Int, height: Int): String? =
        MediaCodecList(MediaCodecList.REGULAR_CODECS)
            .findDecoderForFormat(MediaFormat.createVideoFormat(MIME_TYPE, width, height))

    fun kind(info: MediaCodecInfo): String = when {
        info.isHardwareAccelerated -> "hardware"
        info.isSoftwareOnly -> "software"
        else -> "unclassified"
    }

    fun check(info: MediaCodecInfo, sps: SequenceParameterSet): CapabilityCheck {
        val capabilities = info.getCapabilitiesForType(MIME_TYPE)
        val video = videoCapabilities(capabilities)
        val sizeSupported = video.isSizeSupported(sps.width, sps.height)
        return CapabilityCheck(
            sizeSupported = sizeSupported,
            sizeAndRateSupported = sps.framesPerSecond?.let { rate ->
                sizeSupported && video.areSizeAndRateSupported(sps.width, sps.height, rate)
            },
            profileAndLevelCovered = AvcProfileLevel.isCovered(sps, capabilities.profileLevels.map { it.profile to it.level }),
        )
    }

    private fun videoCapabilities(capabilities: CodecCapabilities): MediaCodecInfo.VideoCapabilities =
        checkNotNull(capabilities.videoCapabilities) { "An H.264 decoder reported no video capabilities." }

    /** A plain-text account of the device and its H.264 decoders, for the screen and the log. */
    fun report(): String = buildString {
        appendLine("Device: ${Build.MANUFACTURER} ${Build.MODEL} (${Build.DEVICE}), Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})")
        appendLine("SoC: ${if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) "${Build.SOC_MANUFACTURER} ${Build.SOC_MODEL}" else Build.HARDWARE}")
        val decoders = h264Decoders()
        appendLine("H.264 decoders reported by MediaCodecList: ${decoders.size}")
        for (info in decoders) {
            appendLine()
            append(describe(info))
        }
    }

    private fun describe(info: MediaCodecInfo): String = buildString {
        val capabilities = info.getCapabilitiesForType(MIME_TYPE)
        val video = videoCapabilities(capabilities)

        appendLine("${info.name}  [${kind(info)}${if (info.isVendor) ", vendor" else ", platform"}]")
        if (info.canonicalName != info.name) appendLine("  alias of ${info.canonicalName}")
        appendLine(
            "  profiles and highest level: " + capabilities.profileLevels.joinToString {
                "${AvcProfileLevel.profileName(it.profile)}@${AvcProfileLevel.levelName(it.level)}"
            },
        )
        appendLine("  width ${video.supportedWidths}, height ${video.supportedHeights}, alignment ${video.widthAlignment}x${video.heightAlignment}")
        appendLine("  frame rate ${video.supportedFrameRates}, bitrate ${video.bitrateRange} bit/s")
        appendLine("  concurrent instances: ${capabilities.maxSupportedInstances}")
        appendLine(
            "  features: " + listOf(
                "adaptive playback" to CodecCapabilities.FEATURE_AdaptivePlayback,
                "low latency" to FEATURE_LOW_LATENCY,
                "secure playback" to CodecCapabilities.FEATURE_SecurePlayback,
                "tunneled playback" to CodecCapabilities.FEATURE_TunneledPlayback,
            ).joinToString { (label, feature) ->
                val state = when {
                    Build.VERSION.SDK_INT < Build.VERSION_CODES.R && feature == FEATURE_LOW_LATENCY -> "unknown before Android 11"
                    capabilities.isFeatureRequired(feature) -> "required"
                    capabilities.isFeatureSupported(feature) -> "yes"
                    else -> "no"
                }
                "$label: $state"
            },
        )

        val points = video.supportedPerformancePoints
        appendLine("  performance points: ${points?.joinToString() ?: "none published"}")

        appendLine("  size and frame rate within reported limits (yes/no), and the frame rates the device publishes for it:")
        for ((width, height) in CANDIDATE_SIZES) {
            if (!video.isSizeSupported(width, height)) {
                appendLine("    ${width}x$height: size not supported")
                continue
            }
            val rates = CANDIDATE_RATES.joinToString { rate ->
                "$rate fps ${if (video.areSizeAndRateSupported(width, height, rate.toDouble())) "yes" else "no"}"
            }
            val achievable = video.getAchievableFrameRatesFor(width, height)
            val limit = video.getSupportedFrameRatesFor(width, height)
            appendLine(
                "    ${width}x$height: $rates; supported rates $limit" +
                    (achievable?.let { "; published as achievable: ${it.lower.roundToInt()}-${it.upper.roundToInt()} fps" } ?: ""),
            )
        }
    }
}
