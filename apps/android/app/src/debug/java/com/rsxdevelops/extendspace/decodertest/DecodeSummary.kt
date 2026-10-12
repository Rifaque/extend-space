package com.rsxdevelops.extendspace.decodertest

import java.util.Locale

/**
 * The plain-text record of one decode run, written to the log and to a file so that the result can
 * be read back over ADB exactly as the device measured it.
 */
internal object DecodeSummary {
    fun format(status: DecodeStatus): String = buildString {
        appendLine("ExtendSpace decoder validation result")
        appendLine("state: ${status.state}")
        appendLine("file: ${status.sampleName ?: "none"} (${status.sampleBytes?.let { "$it bytes" } ?: "size unknown"})")
        val stream = status.stream
        appendLine(
            if (stream == null) {
                "stream: not read"
            } else {
                "stream: ${stream.width} x ${stream.height}, ${stream.profileName} profile, level ${stream.levelName}, " +
                    (stream.framesPerSecond?.let { "${number(it)} fps stated" } ?: "no frame rate stated") +
                    ", ${if (stream.interlaced) "interlaced" else "progressive"}"
            },
        )
        appendLine("decoder: ${status.decoderName ?: "none"}${status.decoderKind?.let { " [$it]" }.orEmpty()}")
        appendLine("decoder chosen by: ${status.decoderChosenBy ?: "n/a"}")
        appendLine("capability check: ${status.capabilityCheck ?: "n/a"}")
        appendLine("low latency: ${status.lowLatency ?: "n/a"}")
        appendLine("pacing: ${if (status.options.paced) "frames shown at the stream's frame rate" else "none, as fast as the device decodes and shows them"}")
        appendLine("decoder output: ${status.outputFormat ?: "n/a"}")
        appendLine("pictures queued: ${status.accessUnitsQueued} (${status.keyFramesQueued} keyframes, ${status.bytesQueued} bytes)")
        appendLine("frames decoded and released to the surface: ${status.framesDecoded}")
        appendLine("frames the system reported as displayed: ${status.framesReportedDisplayed}")
        appendLine("elapsed: ${milliseconds(status.elapsedNanos)} ms")
        appendLine("time to first frame: ${status.timeToFirstFrameNanos?.let { "${milliseconds(it)} ms" } ?: "n/a"}")
        appendLine("frames per second: ${status.framesPerSecond?.let(::number) ?: "n/a"}")
        append("error: ${status.error ?: "none"}")
    }

    fun number(value: Double): String = String.format(Locale.ROOT, "%.2f", value)

    fun milliseconds(nanos: Long): String = String.format(Locale.ROOT, "%.1f", nanos / 1e6)
}
