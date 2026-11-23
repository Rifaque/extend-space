package com.rsxdevelops.extendspace.video

import com.rsxdevelops.extendspace.protocol.Protocol

/**
 * The bookkeeping between a decoder's input buffers and the pictures given to it: the input buffers
 * the codec has offered that have no picture yet, and the pictures waiting for one. Plain Kotlin, so
 * that the policy can be tested off the device; [SurfaceDecoder] calls it under its own lock (it is
 * not thread-safe).
 *
 * **How many may wait (D60).** A decoder that is behind refuses a picture rather than let delay
 * build up; the caller then drops to the next keyframe. Once running, [steadyPictures] may wait. A
 * new decoder is given [startupPictures]: before its first output, and while it works off what
 * arrived meanwhile (including the frames that waited for it to be created, D59). That allowance
 * ends for good at the first of:
 * - the decoder has caught up: it has produced output and fewer than [steadyPictures] wait, so the
 *   steady limit would take the next picture;
 * - [startupNanos] after the codec started, caught up or not, so that a decoder that stalls or never
 *   catches up does not keep it.
 *
 * Until D60 it ended at the first output, while the backlog was still draining. On the Pad 7, 15
 * pictures were waiting at that moment and the next one was refused (section 20.11).
 *
 * In every phase the pictures waiting are also bounded in bytes ([maxBytes]).
 *
 * **A keyframe at capacity (D61).** An IDR picture (protocol: the `KEYFRAME` flag) depends on no
 * earlier picture, and no later picture depends on one before it. One offered when it would
 * otherwise be refused therefore supersedes every picture still waiting: they are dropped (none
 * has reached the codec) and it takes their place. This is the picture that recovers from a
 * refusal, and refusing it too would cost another recovery. Only at capacity: a keyframe that fits
 * waits behind the others like any picture. The caller learns how many were dropped from
 * [takeSuperseded].
 */
internal class InputBacklog<T>(
    private val steadyPictures: Int = STEADY_PICTURES,
    private val startupPictures: Int = STARTUP_PICTURES,
    private val maxBytes: Long = MAX_BYTES,
    private val startupNanos: Long = STARTUP_NANOS,
) {
    enum class Phase {
        /** No output yet. */
        STARTING,

        /** Output, but not yet caught up with what waits. */
        CATCHING_UP,

        /** The steady limit applies, from now on. */
        STEADY,
    }

    sealed interface Offer {
        /** Put the picture straight into this input buffer. */
        data class Input(val index: Int) : Offer

        /** Kept until an input buffer is free. */
        data object Waiting : Offer

        /** Refused: the decoder is too far behind. [reason] says by how much. */
        data class Refused(val reason: String) : Offer
    }

    private var superseded = 0

    private class Entry<T>(val picture: T, val bytes: Int)

    private val freeInputs = ArrayDeque<Int>()
    private val waiting = ArrayDeque<Entry<T>>()
    private val notes = ArrayList<String>(2)
    private var startedNanos = 0L

    var phase = Phase.STARTING
        private set

    /** Pictures waiting for an input buffer now, and their bytes. */
    val depth: Int get() = waiting.size
    var waitingBytes = 0L
        private set

    /** The most pictures that have waited at once, and the most bytes. */
    var mostWaiting = 0
        private set
    var mostWaitingBytes = 0L
        private set

    /** How many pictures may wait now. */
    val limit: Int get() = if (phase == Phase.STEADY) steadyPictures else startupPictures

    /** The codec has started: the startup allowance's time limit counts from here. */
    fun started(nowNanos: Long) {
        startedNanos = nowNanos
    }

    /**
     * A picture of [bytes] bytes to decode; [picture] makes the copy that is kept if it has to wait.
     * [keyFrame]: it is an IDR picture, which supersedes what waits rather than be refused.
     */
    fun offer(nowNanos: Long, bytes: Int, keyFrame: Boolean, picture: () -> T): Offer {
        freeInputs.removeFirstOrNull()?.let { return Offer.Input(it) }
        update(nowNanos)
        val full = waiting.size >= limit || waitingBytes + bytes > maxBytes
        if (full && keyFrame && bytes <= maxBytes) {
            val dropped = waiting.size
            superseded += dropped
            notes += "keyframe superseded $dropped pictures ($waitingBytes bytes) waiting for an input buffer ${millis(nowNanos)} ms after it started, ${phaseText()}"
            clearWaiting()
        } else if (full) {
            return Offer.Refused(
                "${waiting.size} pictures ($waitingBytes bytes) waiting for an input buffer ${millis(nowNanos)} ms after it started; " +
                    "limit $limit pictures and $maxBytes bytes ${phaseText()}; most so far $mostWaiting",
            )
        }
        waiting.addLast(Entry(picture(), bytes))
        waitingBytes += bytes
        mostWaiting = maxOf(mostWaiting, waiting.size)
        mostWaitingBytes = maxOf(mostWaitingBytes, waitingBytes)
        return Offer.Waiting
    }

    /** The codec offers input buffer [index]: the picture to put in it, in order, or null if none waits (the buffer is kept). */
    fun inputAvailable(nowNanos: Long, index: Int): T? {
        val next = waiting.removeFirstOrNull()
        if (next == null) freeInputs.addLast(index) else waitingBytes -= next.bytes
        update(nowNanos)
        return next?.picture
    }

    /** The decoder has produced a picture. */
    fun outputProduced(nowNanos: Long) {
        if (phase != Phase.STARTING) return
        phase = Phase.CATCHING_UP
        notes += "first output ${millis(nowNanos)} ms after it started; ${waiting.size} pictures waiting for an input buffer, most so far $mostWaiting"
        update(nowNanos)
    }

    /** Forgets everything: the decoder is released. */
    fun clear() {
        freeInputs.clear()
        clearWaiting()
    }

    /** Pictures dropped because a keyframe superseded them, since the last call. */
    fun takeSuperseded(): Int = superseded.also { superseded = 0 }

    private fun clearWaiting() {
        waiting.clear()
        waitingBytes = 0
    }

    /** What changed since the last call, for the log: a few lines per decoder at most. */
    fun takeNotes(): List<String> {
        if (notes.isEmpty()) return emptyList()
        return notes.toList().also { notes.clear() }
    }

    private fun update(nowNanos: Long) {
        if (phase == Phase.STEADY) return
        when {
            phase == Phase.CATCHING_UP && waiting.size < steadyPictures -> {
                phase = Phase.STEADY
                notes += "caught up ${millis(nowNanos)} ms after it started: ${waiting.size} pictures waiting, most so far $mostWaiting " +
                    "($mostWaitingBytes bytes); limit now $steadyPictures"
            }
            nowNanos - startedNanos >= startupNanos -> {
                val before = if (phase == Phase.STARTING) "before its first output" else "without catching up"
                phase = Phase.STEADY
                notes += "startup allowance ended ${millis(nowNanos)} ms after it started, $before: ${waiting.size} pictures waiting; limit now $steadyPictures"
            }
        }
    }

    private fun phaseText(): String = when (phase) {
        Phase.STARTING -> "(before its first output)"
        Phase.CATCHING_UP -> "(catching up after its first output)"
        Phase.STEADY -> "(steady)"
    }

    private fun millis(nowNanos: Long) = (nowNanos - startedNanos) / 1_000_000

    companion object {
        /** A few pictures of slack once running; at 60 fps this is 67 ms of delay at most before frames are dropped instead. */
        const val STEADY_PICTURES = 4

        /**
         * Starting a decoder takes on the order of a hundred milliseconds, during which the host
         * keeps sending. Those first pictures are kept rather than dropped, since dropping them
         * costs a keyframe; the decoder works through them far faster than they arrive. Measured:
         * with the ordinary limit, a 1080p60 stream lost its first 17 frames this way.
         */
        const val STARTUP_PICTURES = 30

        /** Two video payloads of the largest size the protocol allows. */
        const val MAX_BYTES = 2L * Protocol.MAX_VIDEO_PAYLOAD

        /**
         * How long the startup allowance may last. On the Pad 7 the first output came 47 ms after
         * the codec started and the backlog was worked off within 131 ms (section 20.11).
         */
        const val STARTUP_NANOS = 1_000_000_000L
    }
}
