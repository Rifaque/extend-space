package com.rsxdevelops.extendspace.video

// Copied from the debug experiment (debug/.../livestream/VideoGate.kt) for the production receiver:
// package changed, and the live screen's status wording left behind (decision D52).

import com.rsxdevelops.extendspace.protocol.Protocol

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
