package com.rsxdevelops.extendspace.livestream.protocol

import java.io.IOException

/**
 * The wire constants of the ExtendSpace protocol, draft 0, as written down in
 * `protocol/specification/protocol.md`. All integers on the wire are big-endian. The Windows
 * experiment has the same constants in `Streaming/Protocol.cs`; the two test suites decode the same
 * byte vectors so that they cannot drift apart unnoticed.
 */
internal object Protocol {
    const val MAJOR_VERSION = 0

    /** ASCII "EXSP" and the framing version. Each side sends it first, before any frame. */
    val PREFACE = byteArrayOf('E'.code.toByte(), 'X'.code.toByte(), 'S'.code.toByte(), 'P'.code.toByte(), 0)

    const val HEADER_LENGTH = 8
    const val VIDEO_HEADER_LENGTH = 12

    const val CHANNEL_CONTROL = 0
    const val CHANNEL_VIDEO = 1
    const val CHANNEL_INPUT = 2

    const val MAX_CONTROL_PAYLOAD = 64 * 1024
    const val MAX_VIDEO_PAYLOAD = 8 * 1024 * 1024
    const val MAX_INPUT_PAYLOAD = 4 * 1024

    /** Frames on a channel this build does not know are skipped, up to this size. */
    const val MAX_SKIPPABLE_PAYLOAD = MAX_VIDEO_PAYLOAD

    const val MAX_JSON_DEPTH = 8

    /** The app's socket in the abstract namespace; `adb forward tcp:<port> localabstract:extendspace` reaches it. */
    const val LOCAL_SOCKET_NAME = "extendspace"

    const val HANDSHAKE_STEP_TIMEOUT_MS = 5_000
    const val KEEPALIVE_TIMEOUT_MS = 5_000
    const val STATS_INTERVAL_MS = 1_000L

    const val FLAG_KEYFRAME = 1
    const val FLAG_CONFIG = 2
}

/** @property length payload size in bytes, as an unsigned 32-bit value held in a Long: it is untrusted input. */
internal data class FrameHeader(val channel: Int, val type: Int, val flags: Int, val length: Long) {
    val isRandomAccessPoint: Boolean
        get() = flags and (Protocol.FLAG_KEYFRAME or Protocol.FLAG_CONFIG) == (Protocol.FLAG_KEYFRAME or Protocol.FLAG_CONFIG)

    fun write(destination: ByteArray, offset: Int = 0) {
        destination[offset] = channel.toByte()
        destination[offset + 1] = type.toByte()
        destination[offset + 2] = (flags ushr 8).toByte()
        destination[offset + 3] = flags.toByte()
        destination[offset + 4] = (length ushr 24).toByte()
        destination[offset + 5] = (length ushr 16).toByte()
        destination[offset + 6] = (length ushr 8).toByte()
        destination[offset + 7] = length.toByte()
    }

    companion object {
        fun read(source: ByteArray, offset: Int = 0): FrameHeader = FrameHeader(
            channel = source[offset].toInt() and 0xFF,
            type = source[offset + 1].toInt() and 0xFF,
            flags = ((source[offset + 2].toInt() and 0xFF) shl 8) or (source[offset + 3].toInt() and 0xFF),
            length = ((source[offset + 4].toLong() and 0xFF) shl 24) or
                ((source[offset + 5].toLong() and 0xFF) shl 16) or
                ((source[offset + 6].toLong() and 0xFF) shl 8) or
                (source[offset + 7].toLong() and 0xFF),
        )
    }
}

/** Control message types, protocol section 14. */
internal object ControlType {
    const val HELLO = 1
    const val CAPABILITIES = 2
    const val STREAM_CONFIG = 3
    const val STREAM_READY = 4
    const val REQUEST_KEYFRAME = 5
    const val PING = 6
    const val PONG = 7
    const val STATS = 8
    const val ERROR = 9
    const val BYE = 10
}

/** The error codes of protocol section 12 that this client can raise. */
internal object ErrorCode {
    const val PROTOCOL_VERSION_UNSUPPORTED = "protocol_version_unsupported"
    const val MALFORMED_MESSAGE = "malformed_message"
    const val MESSAGE_TOO_LARGE = "message_too_large"
    const val UNEXPECTED_MESSAGE = "unexpected_message"
    const val STREAM_CONFIG_REJECTED = "stream_config_rejected"
    const val INTERNAL_ERROR = "internal_error"
}

/** The peer broke the protocol. [code] is what an `error` message would carry. */
internal class ProtocolException(val code: String, message: String) : IOException(message)
