package com.rsxdevelops.extendspace.livestream

import com.rsxdevelops.extendspace.livestream.protocol.Protocol

/** What to do with a video frame that has just arrived. */
internal enum class GateDecision {
    /** Give it to the decoder. */
    DECODE,

    /** There is nowhere to show it. */
    DISCARD_NO_SURFACE,

    /** It depends on pictures that were never decoded; only a keyframe can restart the picture. */
    DISCARD_AWAITING_KEYFRAME,
}

/**
 * Decides which received frames the decoder may be given. H.264 pictures build on earlier ones, so
 * after anything breaks the chain (a frame lost on the way, no surface to decode onto, a decoder
 * error, a decoder too far behind) everything up to the next keyframe is useless and is discarded
 * rather than decoded into garbage.
 *
 * Not thread-safe; the connection's reader thread owns it.
 */
internal class VideoGate {
    private var expectedSequence: Long? = null
    private var awaitingKeyframe = true

    /** Frames the sequence numbers show were sent but never arrived. Read by the statistics timer too. */
    @Volatile
    var framesMissing = 0L
        private set

    /** True from the moment the chain breaks until a keyframe is accepted. A new gate starts this way. */
    val needsKeyframe: Boolean get() = awaitingKeyframe

    /**
     * @param sequence the frame's sequence number, an unsigned 32-bit value.
     * @param flags the frame header's flags.
     */
    fun onFrame(sequence: Long, flags: Int, hasSurface: Boolean): GateDecision {
        val expected = expectedSequence
        if (expected != null && sequence != expected) {
            // Sequence numbers wrap at 2^32. A step backwards means the host started again.
            val gap = (sequence - expected) and SEQUENCE_MASK
            if (gap < SEQUENCE_MASK / 2) framesMissing += gap
            awaitingKeyframe = true
        }
        expectedSequence = (sequence + 1) and SEQUENCE_MASK

        if (!hasSurface) {
            awaitingKeyframe = true
            return GateDecision.DISCARD_NO_SURFACE
        }

        val randomAccessPoint = flags and RANDOM_ACCESS == RANDOM_ACCESS
        if (awaitingKeyframe && !randomAccessPoint) return GateDecision.DISCARD_AWAITING_KEYFRAME

        awaitingKeyframe = false
        return GateDecision.DECODE
    }

    /** The decoder failed, fell too far behind, or lost its surface: nothing decodes until a keyframe. */
    fun breakChain() {
        awaitingKeyframe = true
    }

    private companion object {
        const val SEQUENCE_MASK = 0xFFFF_FFFFL
        const val RANDOM_ACCESS = Protocol.FLAG_KEYFRAME or Protocol.FLAG_CONFIG
    }
}

/**
 * Why a connection ended when its input simply stopped, with no goodbye from the host. When this
 * side closed it, the socket it was reading closes too, and that must not be blamed on the host:
 * measured on a device, a screen closed by the user was logged as the host having hung up.
 */
internal fun describeEndOfInput(stoppedHere: Boolean): String =
    if (stoppedHere) "Stopped: the live screen was closed." else "The host closed the connection without saying goodbye."

/**
 * Why a connection was lost when reading from it failed. A link that stays open and goes silent
 * ends in a read timeout, whose own message is the operating system's "Try again"; measured on a
 * device, that is all the log said. If nothing arrived for as long as the timeout, say that instead.
 */
internal fun describeLostConnection(quietMillis: Long, timeoutMillis: Int, cause: String?): String =
    if (quietMillis >= timeoutMillis) {
        "The connection to the host was lost: nothing arrived from it for ${timeoutMillis / 1000} s."
    } else {
        "The connection to the host was lost: ${cause ?: "the read failed"}."
    }

/** Lets through at most one keyframe request per interval, so a run of discarded frames asks once. */
internal class RequestLimiter(private val minimumIntervalMicros: Long = 500_000) {
    private var lastMicros: Long? = null

    fun tryAcquire(nowMicros: Long): Boolean {
        val last = lastMicros
        if (last != null && nowMicros - last < minimumIntervalMicros) return false
        lastMicros = nowMicros
        return true
    }
}
