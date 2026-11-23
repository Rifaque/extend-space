package com.rsxdevelops.extendspace.protocol

// Copied from the debug experiment (debug/.../livestream/protocol/FrameParser.kt)
// for the production link and receiver, package changed only (migration stage 5, decision D52).
// The debug copy stays until the experiment is retired (stage 8).

/**
 * Receives each complete frame. [payload] is the parser's own buffer and only its first [length]
 * bytes belong to the frame; it is valid during the call and reused afterwards.
 */
internal fun interface FrameHandler {
    fun onFrame(header: FrameHeader, payload: ByteArray, length: Int)
}

/**
 * Turns the bytes of a connection, in whatever pieces they arrive, into protocol frames. It knows
 * nothing about sockets, so every way a stream can be cut up or corrupted can be tested directly.
 *
 * Everything it is given is untrusted. A length is checked against its channel's limit before any
 * memory is set aside for it, so a hostile or broken peer cannot make this allocate more than the
 * largest legitimate frame.
 */
internal class FrameParser(maxVideoPayload: Int = Protocol.MAX_VIDEO_PAYLOAD) {
    private enum class Stage { PREFACE, HEADER, PAYLOAD, SKIPPING }

    private val maxVideoPayload = minOf(maxVideoPayload, Protocol.MAX_VIDEO_PAYLOAD)
    private val fixed = ByteArray(Protocol.HEADER_LENGTH)
    private var stage = Stage.PREFACE
    private var fixedFilled = 0
    private var header = FrameHeader(0, 0, 0, 0)
    private var payload = ByteArray(0)
    private var payloadFilled = 0
    private var toSkip = 0L

    /** True once the peer's preface has arrived and was correct. */
    val prefaceReceived: Boolean get() = stage != Stage.PREFACE

    /** True when the bytes so far end exactly between frames. */
    val isAtFrameBoundary: Boolean get() = stage == Stage.HEADER && fixedFilled == 0

    var framesParsed = 0L
        private set

    var framesSkipped = 0L
        private set

    /**
     * Consumes [length] bytes of [data] from [offset] and calls [handler] once for each frame they
     * complete, in order.
     *
     * @throws ProtocolException if the bytes are not a valid ExtendSpace stream.
     */
    fun append(data: ByteArray, offset: Int, length: Int, handler: FrameHandler) {
        var position = offset
        val end = offset + length
        while (position < end) {
            when (stage) {
                Stage.PREFACE -> {
                    position += fillFixed(data, position, end, Protocol.PREFACE.size)
                    checkPreface()
                }

                Stage.HEADER -> {
                    position += fillFixed(data, position, end, Protocol.HEADER_LENGTH)
                    if (fixedFilled == Protocol.HEADER_LENGTH) {
                        fixedFilled = 0
                        beginFrame(FrameHeader.read(fixed), handler)
                    }
                }

                Stage.PAYLOAD -> {
                    val take = minOf(end - position, header.length.toInt() - payloadFilled)
                    System.arraycopy(data, position, payload, payloadFilled, take)
                    payloadFilled += take
                    position += take
                    if (payloadFilled.toLong() == header.length) emit(handler, payloadFilled)
                }

                Stage.SKIPPING -> {
                    val take = minOf((end - position).toLong(), toSkip).toInt()
                    toSkip -= take
                    position += take
                    if (toSkip == 0L) {
                        framesSkipped++
                        stage = Stage.HEADER
                    }
                }
            }
        }
    }

    /** Call when the connection has closed. Throws if it closed part-way through a frame. */
    fun complete() {
        if (!isAtFrameBoundary) {
            throw ProtocolException(
                ErrorCode.MALFORMED_MESSAGE,
                if (stage == Stage.PREFACE) {
                    "The connection closed before the peer finished its preface."
                } else {
                    "The connection closed in the middle of a frame."
                },
            )
        }
    }

    private fun fillFixed(data: ByteArray, position: Int, end: Int, needed: Int): Int {
        val take = minOf(end - position, needed - fixedFilled)
        System.arraycopy(data, position, fixed, fixedFilled, take)
        fixedFilled += take
        return take
    }

    private fun checkPreface() {
        // Reject on the first wrong byte: there is no reason to wait for the rest of a stranger's data.
        for (i in 0 until fixedFilled) {
            if (fixed[i] != Protocol.PREFACE[i]) {
                throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "The peer did not send the ExtendSpace preface.")
            }
        }
        if (fixedFilled == Protocol.PREFACE.size) {
            fixedFilled = 0
            stage = Stage.HEADER
        }
    }

    private fun beginFrame(next: FrameHeader, handler: FrameHandler) {
        val limit: Int? = when (next.channel) {
            Protocol.CHANNEL_CONTROL -> Protocol.MAX_CONTROL_PAYLOAD
            Protocol.CHANNEL_VIDEO -> maxVideoPayload
            Protocol.CHANNEL_INPUT -> Protocol.MAX_INPUT_PAYLOAD
            else -> null
        }

        if (next.length > (limit ?: Protocol.MAX_SKIPPABLE_PAYLOAD)) {
            throw ProtocolException(
                ErrorCode.MESSAGE_TOO_LARGE,
                "A frame on channel ${next.channel} announces ${next.length} bytes; the limit is ${limit ?: Protocol.MAX_SKIPPABLE_PAYLOAD}.",
            )
        }

        header = next
        if (limit == null) {
            // An unknown channel: pass over it without keeping a byte.
            toSkip = next.length
            if (toSkip == 0L) framesSkipped++ else stage = Stage.SKIPPING
            return
        }

        if (next.length == 0L) {
            emit(handler, 0)
            return
        }

        if (payload.size < next.length) {
            // Sized to the frame, which is within its limit, and kept for the frames that follow.
            payload = ByteArray(next.length.toInt())
        }
        payloadFilled = 0
        stage = Stage.PAYLOAD
    }

    private fun emit(handler: FrameHandler, length: Int) {
        stage = Stage.HEADER
        framesParsed++
        handler.onFrame(header, payload, length)
    }
}
