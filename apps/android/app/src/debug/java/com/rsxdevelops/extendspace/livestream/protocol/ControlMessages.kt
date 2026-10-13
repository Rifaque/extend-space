package com.rsxdevelops.extendspace.livestream.protocol

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

internal sealed interface ControlMessage

/** First message in each direction: which protocol versions the sender speaks, and who it is. */
internal data class Hello(
    val protocolMin: Int,
    val protocolMax: Int,
    val role: String,
    val deviceName: String,
    val appVersion: String,
) : ControlMessage

internal data class DecoderCapability(
    val name: String,
    val hardware: Boolean,
    val lowLatency: Boolean,
    val maxWidth: Int,
    val maxHeight: Int,
)

/** What this device can show and decode. The host chooses within it. */
internal data class ClientCapabilities(
    val displayWidth: Int,
    val displayHeight: Int,
    val refreshHz: Double,
    val decoders: List<DecoderCapability>,
) : ControlMessage

/** What the host is about to send. */
internal data class StreamConfig(
    val codec: String,
    val profile: String,
    val width: Int,
    val height: Int,
    val frameRate: Int,
    val bitrate: Int,
    val maxVideoPayload: Int,
    val encoder: String?,
    val encoderAcceleration: String?,
) : ControlMessage

internal data class StreamReady(val decoder: String?, val hardware: Boolean?) : ControlMessage

internal data class KeyframeRequest(val reason: String) : ControlMessage

/**
 * @property hostTimeMicros the host's monotonic clock when the ping was written.
 * @property clockOffsetMicros the host's estimate of "this device's clock minus the host's", which
 *   lets this device express its own event times on the host's clock. Absent in the first pings.
 * @property roundTripMicros the round trip that estimate was taken from; it is good to half of this.
 */
internal data class Ping(
    val id: Long,
    val hostTimeMicros: Long,
    val clockOffsetMicros: Long?,
    val roundTripMicros: Long?,
) : ControlMessage

internal data class Pong(val id: Long, val hostTimeMicros: Long, val clientTimeMicros: Long) : ControlMessage

/** Delays over one reporting interval, in milliseconds, from capture on the host to an event here. */
internal data class LatencySummary(
    val count: Int,
    val minMs: Double,
    val meanMs: Double,
    val p95Ms: Double,
    val maxMs: Double,
)

/** This device's counters, cumulative since the connection was accepted, and its latest latency figures. */
internal data class ClientStats(
    val framesReceived: Long = 0,
    val bytesReceived: Long = 0,
    /** Frames the sequence numbers show were never received. */
    val framesMissing: Long = 0,
    /** Received but not given to the decoder: no surface, waiting for a keyframe, or backlog. */
    val framesDiscarded: Long = 0,
    val framesSubmitted: Long = 0,
    val framesDecoded: Long = 0,
    /** Decoded frames released to the surface. */
    val framesRendered: Long = 0,
    /** Frames the system reported as shown. Android documents this as informational. */
    val framesDisplayed: Long = 0,
    val decoderErrors: Long = 0,
    val queueDepth: Int = 0,
    val receiveLatency: LatencySummary? = null,
    val renderLatency: LatencySummary? = null,
    val displayLatency: LatencySummary? = null,
) : ControlMessage

internal data class ErrorMessage(val code: String, val message: String, val fatal: Boolean) : ControlMessage

internal data class Bye(val reason: String) : ControlMessage

/**
 * Control messages are small UTF-8 JSON objects. Unknown fields are ignored; a missing required
 * field, a wrong type or nesting deeper than eight levels is a `malformed_message` error.
 *
 * Field types are checked explicitly rather than left to the JSON library's coercions, so that the
 * library on a device and the one unit tests run against accept exactly the same messages.
 */
internal object ControlCodec {
    /** 2^63: at or beyond it a JSON number does not fit the 64-bit integers the protocol uses. */
    private const val WHOLE_NUMBER_LIMIT = 9.223372036854775807E18

    /** A complete control frame: header and JSON payload. */
    fun encode(message: ControlMessage): ByteArray {
        val json = JSONObject()
        val type = write(json, message)
        val payload = json.toString().toByteArray(Charsets.UTF_8)
        check(payload.size <= Protocol.MAX_CONTROL_PAYLOAD) { "A control message of ${payload.size} bytes exceeds the channel limit." }

        val frame = ByteArray(Protocol.HEADER_LENGTH + payload.size)
        FrameHeader(Protocol.CHANNEL_CONTROL, type, 0, payload.size.toLong()).write(frame)
        System.arraycopy(payload, 0, frame, Protocol.HEADER_LENGTH, payload.size)
        return frame
    }

    /**
     * The message a control frame carries, or null if its type is one this build does not know.
     *
     * @throws ProtocolException if the payload is not the JSON the type requires.
     */
    fun decode(type: Int, payload: ByteArray, length: Int = payload.size): ControlMessage? {
        if (type !in ControlType.HELLO..ControlType.BYE) return null

        val text = String(payload, 0, length, Charsets.UTF_8)
        if (nestingDepth(text) > Protocol.MAX_JSON_DEPTH) {
            throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "A control message is nested deeper than ${Protocol.MAX_JSON_DEPTH} levels.")
        }

        try {
            return read(type, JSONObject(text))
        } catch (exception: JSONException) {
            throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "Control message type $type is not the JSON it must be: ${exception.message}")
        }
    }

    /** How deeply objects and arrays are nested in [json], ignoring brackets inside strings. */
    internal fun nestingDepth(json: String): Int {
        var depth = 0
        var deepest = 0
        var inString = false
        var escaped = false
        for (character in json) {
            when {
                escaped -> escaped = false
                inString -> when (character) {
                    '\\' -> escaped = true
                    '"' -> inString = false
                }
                character == '"' -> inString = true
                character == '{' || character == '[' -> deepest = maxOf(deepest, ++depth)
                character == '}' || character == ']' -> depth--
            }
        }
        return deepest
    }

    private fun write(json: JSONObject, message: ControlMessage): Int = when (message) {
        is Hello -> {
            json.put("protocol_min", message.protocolMin)
            json.put("protocol_max", message.protocolMax)
            json.put("role", message.role)
            json.put("device_name", message.deviceName)
            json.put("app_version", message.appVersion)
            ControlType.HELLO
        }

        is ClientCapabilities -> {
            json.put(
                "display",
                JSONObject()
                    .put("width", message.displayWidth)
                    .put("height", message.displayHeight)
                    .put("refresh_hz", message.refreshHz),
            )
            val decoders = JSONArray()
            for (decoder in message.decoders) {
                decoders.put(
                    JSONObject()
                        .put("codec", "h264")
                        .put("name", decoder.name)
                        .put("hardware", decoder.hardware)
                        .put("low_latency", decoder.lowLatency)
                        .put("max_width", decoder.maxWidth)
                        .put("max_height", decoder.maxHeight),
                )
            }
            json.put("decoders", decoders)
            ControlType.CAPABILITIES
        }

        is StreamConfig -> {
            json.put("codec", message.codec)
            json.put("profile", message.profile)
            json.put("width", message.width)
            json.put("height", message.height)
            json.put("frame_rate", message.frameRate)
            json.put("bitrate", message.bitrate)
            json.put("max_video_payload", message.maxVideoPayload)
            message.encoder?.let { json.put("encoder", it) }
            message.encoderAcceleration?.let { json.put("encoder_acceleration", it) }
            ControlType.STREAM_CONFIG
        }

        is StreamReady -> {
            message.decoder?.let { json.put("decoder", it) }
            message.hardware?.let { json.put("hardware", it) }
            ControlType.STREAM_READY
        }

        is KeyframeRequest -> {
            json.put("reason", message.reason)
            ControlType.REQUEST_KEYFRAME
        }

        is Ping -> {
            json.put("id", message.id)
            json.put("host_time_us", message.hostTimeMicros)
            message.clockOffsetMicros?.let { json.put("clock_offset_us", it) }
            message.roundTripMicros?.let { json.put("round_trip_us", it) }
            ControlType.PING
        }

        is Pong -> {
            json.put("id", message.id)
            json.put("host_time_us", message.hostTimeMicros)
            json.put("client_time_us", message.clientTimeMicros)
            ControlType.PONG
        }

        is ClientStats -> {
            json.put("frames_received", message.framesReceived)
            json.put("bytes_received", message.bytesReceived)
            json.put("frames_missing", message.framesMissing)
            json.put("frames_discarded", message.framesDiscarded)
            json.put("frames_submitted", message.framesSubmitted)
            json.put("frames_decoded", message.framesDecoded)
            json.put("frames_rendered", message.framesRendered)
            json.put("frames_displayed", message.framesDisplayed)
            json.put("decoder_errors", message.decoderErrors)
            json.put("queue_depth", message.queueDepth)
            putLatency(json, "receive_latency_ms", message.receiveLatency)
            putLatency(json, "render_latency_ms", message.renderLatency)
            putLatency(json, "display_latency_ms", message.displayLatency)
            ControlType.STATS
        }

        is ErrorMessage -> {
            json.put("code", message.code)
            json.put("message", message.message)
            json.put("fatal", message.fatal)
            ControlType.ERROR
        }

        is Bye -> {
            json.put("reason", message.reason)
            ControlType.BYE
        }
    }

    private fun read(type: Int, json: JSONObject): ControlMessage = when (type) {
        ControlType.HELLO -> Hello(
            protocolMin = json.requireInt("protocol_min"),
            protocolMax = json.requireInt("protocol_max"),
            role = json.optionalString("role").orEmpty(),
            deviceName = json.optionalString("device_name").orEmpty(),
            appVersion = json.optionalString("app_version").orEmpty(),
        )

        ControlType.CAPABILITIES -> {
            val display = json.requireObject("display")
            val decoders = json.opt("decoders") as? JSONArray ?: throw missing("decoders")
            ClientCapabilities(
                displayWidth = display.requireInt("width"),
                displayHeight = display.requireInt("height"),
                refreshHz = (display.opt("refresh_hz") as? Number)?.toDouble() ?: 0.0,
                decoders = (0 until decoders.length())
                    .map { decoders.opt(it) as? JSONObject ?: throw missing("decoders[$it]") }
                    .filter { it.optionalString("codec").let { codec -> codec == null || codec == "h264" } }
                    .map {
                        DecoderCapability(
                            name = it.requireString("name"),
                            hardware = it.requireBoolean("hardware"),
                            lowLatency = it.opt("low_latency") == true,
                            maxWidth = it.requireInt("max_width"),
                            maxHeight = it.requireInt("max_height"),
                        )
                    },
            )
        }

        ControlType.STREAM_CONFIG -> StreamConfig(
            codec = json.requireString("codec"),
            profile = json.requireString("profile"),
            width = json.requireInt("width"),
            height = json.requireInt("height"),
            frameRate = json.requireInt("frame_rate"),
            bitrate = json.requireInt("bitrate"),
            maxVideoPayload = json.requireInt("max_video_payload"),
            encoder = json.optionalString("encoder"),
            encoderAcceleration = json.optionalString("encoder_acceleration"),
        )

        ControlType.STREAM_READY -> StreamReady(json.optionalString("decoder"), json.opt("hardware") as? Boolean)

        ControlType.REQUEST_KEYFRAME -> KeyframeRequest(json.optionalString("reason") ?: "unspecified")

        ControlType.PING -> Ping(
            id = json.requireLong("id"),
            hostTimeMicros = json.requireLong("host_time_us"),
            clockOffsetMicros = json.optionalLong("clock_offset_us"),
            roundTripMicros = json.optionalLong("round_trip_us"),
        )

        ControlType.PONG -> Pong(
            id = json.requireLong("id"),
            hostTimeMicros = json.requireLong("host_time_us"),
            clientTimeMicros = json.requireLong("client_time_us"),
        )

        ControlType.STATS -> ClientStats(
            framesReceived = json.requireLong("frames_received"),
            bytesReceived = json.requireLong("bytes_received"),
            framesMissing = json.optionalLong("frames_missing") ?: 0,
            framesDiscarded = json.optionalLong("frames_discarded") ?: 0,
            framesSubmitted = json.optionalLong("frames_submitted") ?: 0,
            framesDecoded = json.requireLong("frames_decoded"),
            framesRendered = json.requireLong("frames_rendered"),
            framesDisplayed = json.optionalLong("frames_displayed") ?: 0,
            decoderErrors = json.optionalLong("decoder_errors") ?: 0,
            queueDepth = (json.optionalLong("queue_depth") ?: 0).toInt(),
            receiveLatency = readLatency(json, "receive_latency_ms"),
            renderLatency = readLatency(json, "render_latency_ms"),
            displayLatency = readLatency(json, "display_latency_ms"),
        )

        ControlType.ERROR -> ErrorMessage(
            code = json.requireString("code"),
            message = json.optionalString("message").orEmpty(),
            fatal = json.opt("fatal") == true,
        )

        ControlType.BYE -> Bye(json.optionalString("reason") ?: "unspecified")

        else -> throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "Control type $type has no reader.")
    }

    private fun putLatency(json: JSONObject, name: String, latency: LatencySummary?) {
        if (latency == null) return
        json.put(
            name,
            JSONObject()
                .put("count", latency.count)
                .put("min", latency.minMs)
                .put("mean", latency.meanMs)
                .put("p95", latency.p95Ms)
                .put("max", latency.maxMs),
        )
    }

    private fun readLatency(json: JSONObject, name: String): LatencySummary? {
        val latency = json.opt(name) as? JSONObject ?: return null
        return LatencySummary(
            count = latency.requireInt("count"),
            minMs = latency.requireDouble("min"),
            meanMs = latency.requireDouble("mean"),
            p95Ms = latency.requireDouble("p95"),
            maxMs = latency.requireDouble("max"),
        )
    }

    private fun missing(name: String) =
        ProtocolException(ErrorCode.MALFORMED_MESSAGE, "A control message has a missing or mistyped field '$name'.")

    private fun JSONObject.requireObject(name: String): JSONObject = opt(name) as? JSONObject ?: throw missing(name)

    private fun JSONObject.requireString(name: String): String = opt(name) as? String ?: throw missing(name)

    private fun JSONObject.optionalString(name: String): String? = opt(name) as? String

    private fun JSONObject.requireBoolean(name: String): Boolean = opt(name) as? Boolean ?: throw missing(name)

    private fun JSONObject.requireDouble(name: String): Double = (opt(name) as? Number)?.toDouble() ?: throw missing(name)

    private fun JSONObject.requireLong(name: String): Long = optionalLong(name) ?: throw missing(name)

    private fun JSONObject.requireInt(name: String): Int {
        val value = requireLong(name)
        if (value < Int.MIN_VALUE || value > Int.MAX_VALUE) throw missing(name)
        return value.toInt()
    }

    /** A whole number, or null when the field is absent or is anything else, including 1.5 and "1". */
    private fun JSONObject.optionalLong(name: String): Long? = when (val value = opt(name)) {
        is Int -> value.toLong()
        is Long -> value
        // Doubles, and the big-number types some JSON libraries produce for long or fractional digits.
        is Number -> {
            val asDouble = value.toDouble()
            if (asDouble % 1.0 == 0.0 && asDouble > -WHOLE_NUMBER_LIMIT && asDouble < WHOLE_NUMBER_LIMIT) value.toLong() else null
        }
        else -> null
    }
}
