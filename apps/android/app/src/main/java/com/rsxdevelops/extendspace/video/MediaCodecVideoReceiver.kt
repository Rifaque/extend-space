package com.rsxdevelops.extendspace.video

import com.rsxdevelops.extendspace.connection.AttemptOutcome
import com.rsxdevelops.extendspace.connection.DecoderCapability
import com.rsxdevelops.extendspace.connection.DecoderChoice
import com.rsxdevelops.extendspace.connection.FaultCategory
import com.rsxdevelops.extendspace.connection.FaultTreatment
import com.rsxdevelops.extendspace.connection.SessionFault
import com.rsxdevelops.extendspace.connection.StreamDescription
import com.rsxdevelops.extendspace.connection.VideoOutput
import com.rsxdevelops.extendspace.connection.VideoReceiver
import com.rsxdevelops.extendspace.connection.VideoSink
import com.rsxdevelops.extendspace.protocol.ClientStats
import com.rsxdevelops.extendspace.protocol.ErrorCode
import com.rsxdevelops.extendspace.protocol.Protocol
import com.rsxdevelops.extendspace.protocol.ProtocolException
import com.rsxdevelops.extendspace.video.h264.AnnexB
import com.rsxdevelops.extendspace.video.h264.MalformedStreamException
import com.rsxdevelops.extendspace.video.h264.NalType
import com.rsxdevelops.extendspace.video.h264.SequenceParameterSet
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext
import java.util.ArrayDeque
import java.util.concurrent.Executor
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/**
 * The production [VideoReceiver]: the debug experiment's receive path (`LiveSession.onVideo`,
 * `prepareDecoder`, `VideoGate`, `LiveDecoder`) behind the session contract, with the socket and the
 * session state left to the link and the controller (architecture section 7).
 *
 * **Threads (D59).** [sink] runs on the link's reader thread and decides what each frame is for.
 * [setOutput] runs on the UI thread. [start], [stop], [prepare] and [decoders] are called on the
 * session controller's thread. Decoders are created on the decoder owner ([owner]), which runs one
 * task at a time: no other thread waits for a creation, so a slow `MediaCodec` start holds up
 * neither the protocol nor the UI.
 *
 * **Who holds a decoder.** Exactly one holder at a time, so no two operations ever touch one decoder
 * at once. The owner, while creating it. The receiver's lock, from when the owner publishes it until
 * it is taken out of use: every frame is submitted under the lock, and the lock is only ever held
 * briefly. Then whoever took it out of use, to release it: the UI thread for a withdrawn or replaced
 * output (before [setOutput] returns, because the caller is about to destroy the output), the owner
 * for everything else (a new stream, a stop, a decoder error, other parameter sets).
 *
 * **Stale creations.** A creation is for one stream and one generation of the output, and it is
 * published only if, when it returns, it is still the one wanted: a withdrawal, a replaced output, a
 * stop, a new stream or a keyframe with other parameter sets each abandon it. A stale decoder is
 * released by the owner without ever being given a frame; a stale failure is only logged. Only the
 * wanted creation's failure ends the stream (terminal `decoder_start_failed`).
 *
 * **Frames while a decoder is being created** are kept, copied, in order, up to [MAX_WAITING_FRAMES]
 * and [MAX_WAITING_BYTES], and submitted when it is published, before any later frame. One frame
 * more and all of them are discarded, the chain is broken and a keyframe asked for
 * (`decoder_backlog`), as when a running decoder falls too far behind. Nothing counts as submitted
 * until a decoder has taken it.
 *
 * **Stopping** takes the decoder out of use and abandons any creation at once, then waits, suspended
 * and without being cancellable, for the owner to have released everything (D58): the controller's
 * thread is never held, and its teardown deadline still applies if a `MediaCodec` call never returns.
 *
 * **Keyframes (D61).** Frames that cannot be decoded until a keyframe arrives (after a sequence
 * gap, a decoder error, a backlog, or a decoder released with its output) make it call
 * `onKeyframeNeeded`: once per recovery, at once, and again only if no `KEYFRAME | CONFIG` frame has
 * arrived [RE_ASK_MICROS] after the last request. The stream owns that state; the link sends every
 * request it is given, and the host rate-limits them without losing any (protocol section 10), so
 * no second clock on this side delays a recovery. It does not ask when an output is lent or a
 * stream starts: that is the controller's rule (D38). A keyframe offered to a decoder that is
 * behind supersedes the pictures waiting in it instead of being refused, and one that arrives while
 * a decoder is being created and its frames would overflow replaces them; the frames dropped are
 * counted as discarded, not submitted.
 *
 * **Malformed video.** A video payload too short for its header, or a keyframe flagged CONFIG without
 * an SPS and a PPS, is a protocol violation: [sink] throws [ProtocolException] and the link ends the
 * connection with a fatal `malformed_message`, as the experiment did.
 */
internal class MediaCodecVideoReceiver(
    private val platform: DecoderPlatform,
    /** The host's clock relation, which the link keeps up to date from pings; used for latency only. */
    private val hostClock: HostClockEstimate = HostClockEstimate(),
    private val log: (String) -> Unit = {},
    private val nowMicros: () -> Long = { System.nanoTime() / 1_000 },
    /**
     * Runs on the reader thread after the gate has let a keyframe through and before the decoder
     * lock is taken: the window in which the output can be withdrawn (D55). Does nothing outside
     * tests, which use it to put a withdrawal exactly there.
     */
    private val beforeDecoderLock: () -> Unit = {},
    /**
     * The decoder owner: where decoders are created and released. It must run its tasks one at a
     * time, in order. The receiver never queues more than one task on it. Tests pass one that runs
     * each task at once on the calling thread, or one they step by hand.
     */
    private val owner: Executor = decoderOwner(),
) : VideoReceiver {
    private val lock = Any()

    // Under [lock].
    private var output: VideoOutput? = null
    private var outputGeneration = 0L
    private var decoder: FrameDecoder? = null
    private var decoderCreation: Creation? = null
    private var wanted: Creation? = null
    private val toRelease = ArrayDeque<Retired>()
    private val ownerIdle = mutableListOf<CompletableDeferred<Unit>>()
    private var ownerScheduled = false

    @Volatile private var current: Stream? = null

    private val receiveLatency = LatencyTracker()
    private val renderLatency = LatencyTracker()

    /** One stream, from [start] to [stop]. Its gate is the reader thread's alone. */
    private inner class Stream(
        val description: StreamDescription,
        val choice: DecoderChoice,
        val onKeyframeNeeded: (String) -> Unit,
    ) {
        val fault = CompletableDeferred<SessionFault?>()
        val gate = VideoGate()

        /** When the last keyframe request of this recovery was made; null when no recovery is waiting. Reader thread only. */
        var keyframeAskedMicros: Long? = null
        var chainBreakReason = REASON_STARTUP

        /** A decoder's failure, reported on its own thread and handled on the reader thread at the next frame. */
        @Volatile var decoderFailure: Pair<Creation, String>? = null

        /** Set by the owner when a decoder it published refused the frames that waited for it. */
        @Volatile var ownerBrokeChain: String? = null

        val framesReceived = AtomicLong()
        val bytesReceived = AtomicLong()
        val framesDiscarded = AtomicLong()
        val framesSubmitted = AtomicLong()
        val framesRendered = AtomicLong()
        val framesDisplayed = AtomicLong()
        val decoderErrors = AtomicLong()
        val keyframesReceived = AtomicLong()
        val decodersCreated = AtomicLong()

        @Volatile var startedMicros = 0L
        @Volatile var firstKeyframeMicros = -1L
        @Volatile var firstRenderedMicros = -1L

        fun frameRendered(timestampMicros: Long, releasedNanos: Long) {
            if (framesRendered.incrementAndGet() == 1L) {
                firstRenderedMicros = releasedNanos / 1_000
                log("first frame rendered ${(firstRenderedMicros - startedMicros) / 1_000} ms after the stream started")
            }
            hostClock.latencyMicros(releasedNanos / 1_000, timestampMicros)?.let(renderLatency::add)
        }
    }

    /**
     * One decoder wanted for one stream on one generation of the output. It is also the decoder's
     * listener, so that a decoder's failure is known to be its own and not its successor's.
     */
    private inner class Creation(
        val stream: Stream,
        val output: VideoOutput,
        val outputGeneration: Long,
        val parameters: SequenceParameterSet,
        val sps: ByteArray,
        val pps: ByteArray,
    ) : FrameDecoderListener {
        val configuredWith: ByteArray = sps + pps
        val requestedMicros = nowMicros()

        // Under [lock].
        var started = false
        val waiting = ArrayDeque<Waiting>()
        var waitingBytes = 0L
        var arrived = 0L

        override fun onFrameRendered(timestampMicros: Long, releasedNanos: Long) = stream.frameRendered(timestampMicros, releasedNanos)

        override fun onFrameDisplayed(timestampMicros: Long) {
            stream.framesDisplayed.incrementAndGet()
        }

        override fun onDecoderError(message: String) {
            stream.decoderFailure = this to message
        }

        override fun onDecoderNote(message: String) = log(message)
    }

    /** A frame kept for a decoder still being created; a copy, since the link reuses its buffer. */
    private class Waiting(val data: ByteArray, val timestampMicros: Long, val keyFrame: Boolean)

    /** A decoder taken out of use, for the owner to release. */
    private class Retired(val decoder: FrameDecoder, val reason: String)

    private class ParameterSets(val parameters: SequenceParameterSet, val sps: ByteArray, val pps: ByteArray) {
        val configuredWith: ByteArray = sps + pps
    }

    private enum class Delivery { SUBMITTED, WAITING, REFUSED, OVERFLOW, NO_DECODER }

    override val sink = VideoSink { flags, payload, length -> current?.let { onVideo(it, flags, payload, length) } }

    override suspend fun decoders(): List<DecoderCapability> = platform.decoders()

    override suspend fun prepare(stream: StreamDescription): AttemptOutcome<DecoderChoice> {
        if (stream.codec != CODEC_H264) {
            return AttemptOutcome.Failed(decoderFault(CODE_CODEC_UNSUPPORTED, "This app decodes h264 only; the PC offered '${stream.codec}'."))
        }
        if (stream.width <= 0 || stream.height <= 0) {
            return AttemptOutcome.Failed(decoderFault(CODE_DECODER_UNAVAILABLE, "The PC offered a ${stream.width} x ${stream.height} stream."))
        }
        val choice = platform.choose(stream.width, stream.height)
            ?: return AttemptOutcome.Failed(decoderFault(CODE_DECODER_UNAVAILABLE, "No H.264 decoder on this device reports support for ${stream.width} x ${stream.height}."))
        return AttemptOutcome.Succeeded(choice)
    }

    override fun start(stream: StreamDescription, decoder: DecoderChoice, onKeyframeNeeded: (reason: String) -> Unit) {
        val started = Stream(stream, decoder, onKeyframeNeeded).apply { startedMicros = nowMicros() }
        receiveLatency.drain()
        renderLatency.drain()
        // Nothing is waited for here: whatever the last stream left is the owner's to release.
        val retired = synchronized(lock) {
            wanted?.let { abandonLocked(it, "a new stream started") }
            current = started
            retireDecoderLocked("a new stream started")
        }
        if (retired) scheduleOwner()
        log("stream started: ${stream.width} x ${stream.height} at ${stream.frameRate} fps from ${stream.hostName}; decoder ${decoder.name}")
    }

    override fun setOutput(output: VideoOutput?) {
        // On the UI thread, which is about to destroy the old output: the decoder drawing to it is
        // released before this returns. A decoder still being created for it is not waited for. It
        // is abandoned here and released by the owner when its creation returns, and it is never
        // given a frame (D59).
        val reason = if (output == null) "output withdrawn" else "output replaced"
        val inUse: FrameDecoder?
        synchronized(lock) {
            if (this.output === output) return
            this.output = output
            outputGeneration++
            wanted?.let { abandonLocked(it, reason) }
            inUse = decoder
            decoder = null
            decoderCreation = null
        }
        inUse?.let { release(it, reason) }
    }

    override suspend fun awaitFault(): SessionFault? = current?.fault?.await()

    override suspend fun stop() {
        val released = CompletableDeferred<Unit>()
        val stopped: Stream? = synchronized(lock) {
            current.also {
                current = null
                wanted?.let { creation -> abandonLocked(creation, "stream stopped") }
                retireDecoderLocked("stream stopped")
                ownerIdle.add(released)
            }
        }
        scheduleOwner()
        // Suspended, so the controller's thread is free and its teardown deadline can run while a
        // creation or a release that does not return holds the owner (D58). Never cut short for a
        // cancelled caller: the decoder must go whatever happens.
        withContext(NonCancellable) { released.await() }
        if (stopped != null) {
            stopped.fault.complete(null)
            log("stream stopped: ${summary(stopped)}")
        }
    }

    /** This stream's counters for the `stats` message, with latency since the last call. Null with no stream. */
    fun stats(): ClientStats? {
        val stream = current ?: return null
        val rendered = stream.framesRendered.get()
        return ClientStats(
            framesReceived = stream.framesReceived.get(),
            bytesReceived = stream.bytesReceived.get(),
            framesMissing = stream.gate.framesMissing,
            framesDiscarded = stream.framesDiscarded.get(),
            framesSubmitted = stream.framesSubmitted.get(),
            // Every decoded frame is released to the surface at once, so the two counts are one.
            framesDecoded = rendered,
            framesRendered = rendered,
            framesDisplayed = stream.framesDisplayed.get(),
            decoderErrors = stream.decoderErrors.get(),
            // Pictures waiting: in the decoder, or for one being created. The lock is only ever held briefly.
            queueDepth = synchronized(lock) { decoder?.queueDepth ?: wanted?.waiting?.size ?: 0 },
            receiveLatency = receiveLatency.drain(),
            renderLatency = renderLatency.drain(),
        )
    }

    /** Whether a decoder is in use right now. For tests and diagnostics. */
    val hasDecoder: Boolean get() = synchronized(lock) { decoder != null }

    /** Whether a decoder is wanted and not yet published. For tests and diagnostics. */
    val isCreatingDecoder: Boolean get() = synchronized(lock) { wanted != null }

    // ---- The reader thread ---------------------------------------------------------------------

    private fun onVideo(stream: Stream, flags: Int, payload: ByteArray, length: Int) {
        if (length <= Protocol.VIDEO_HEADER_LENGTH) {
            throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "A video frame of $length bytes is too short to hold its header and a picture.")
        }

        val arrivedMicros = nowMicros()
        val sequence = readUnsignedInt(payload, 0)
        val timestampMicros = readLong(payload, 4)
        val dataOffset = Protocol.VIDEO_HEADER_LENGTH
        val dataLength = length - dataOffset

        stream.framesReceived.incrementAndGet()
        stream.bytesReceived.addAndGet((Protocol.HEADER_LENGTH + length).toLong())
        hostClock.latencyMicros(arrivedMicros, timestampMicros)?.let(receiveLatency::add)
        val randomAccessPoint = flags and RANDOM_ACCESS == RANDOM_ACCESS
        if (randomAccessPoint && stream.keyframesReceived.incrementAndGet() == 1L) {
            stream.firstKeyframeMicros = arrivedMicros
            log("first keyframe received ${(arrivedMicros - stream.startedMicros) / 1_000} ms after the stream started (sequence $sequence)")
        }
        stream.keyframeAskedMicros?.let { asked ->
            if (randomAccessPoint) {
                stream.keyframeAskedMicros = null
                log("keyframe arrived ${(arrivedMicros - asked) / 1_000} ms after the last request (sequence $sequence)")
            }
        }

        // A decoder that failed on its own thread is taken out of use here, on the thread that feeds
        // it; the owner releases it. A failure of a decoder no longer in use changes nothing.
        stream.decoderFailure?.let { (failed, message) ->
            stream.decoderFailure = null
            stream.decoderErrors.incrementAndGet()
            log(message)
            val retired = synchronized(lock) { decoderCreation === failed && retireDecoderLocked("decoder error") }
            if (retired) {
                scheduleOwner()
                stream.gate.breakChain()
                stream.chainBreakReason = REASON_DECODE_ERROR
            }
        }
        stream.ownerBrokeChain?.let { reason ->
            stream.ownerBrokeChain = null
            stream.gate.breakChain()
            stream.chainBreakReason = reason
        }

        val hasOutput = synchronized(lock) { output != null }
        val missingBefore = stream.gate.framesMissing
        val decision = stream.gate.onFrame(sequence, flags, hasOutput)
        if (stream.gate.framesMissing != missingBefore) stream.chainBreakReason = REASON_SEQUENCE_GAP

        when (decision) {
            GateDecision.DISCARD_NO_SURFACE -> {
                stream.framesDiscarded.incrementAndGet()
                stream.chainBreakReason = REASON_SURFACE_RECREATED
            }

            GateDecision.DISCARD_AWAITING_KEYFRAME -> {
                stream.framesDiscarded.incrementAndGet()
                askForKeyframe(stream, arrivedMicros)
            }

            GateDecision.DECODE -> {
                val parameterSets = if (randomAccessPoint) parameterSets(payload, dataOffset, dataLength) else null
                when (deliver(stream, parameterSets, payload, dataOffset, dataLength, timestampMicros, flags and Protocol.FLAG_KEYFRAME != 0)) {
                    Delivery.SUBMITTED -> stream.framesSubmitted.incrementAndGet()
                    Delivery.WAITING -> Unit
                    Delivery.REFUSED -> {
                        // A decoder too far behind: drop to the next keyframe rather than build up delay.
                        stream.framesDiscarded.incrementAndGet()
                        breakChain(stream, REASON_DECODER_BACKLOG, arrivedMicros)
                    }
                    // The frames that waited were counted as discarded when they were dropped.
                    Delivery.OVERFLOW -> breakChain(stream, REASON_DECODER_BACKLOG, arrivedMicros)
                    Delivery.NO_DECODER -> {
                        // The output went away after the gate looked, or the stream was stopped meanwhile.
                        stream.framesDiscarded.incrementAndGet()
                        breakChain(stream, REASON_SURFACE_RECREATED, arrivedMicros)
                    }
                }
            }
        }
    }

    /**
     * Gives one frame the gate let through to the decoder in use, or to the one being created; a
     * keyframe whose parameter sets neither has asks for a new one. Under the lock, briefly: a
     * submission copies the frame and returns.
     */
    private fun deliver(
        stream: Stream,
        parameterSets: ParameterSets?,
        payload: ByteArray,
        offset: Int,
        length: Int,
        timestampMicros: Long,
        keyFrame: Boolean,
    ): Delivery {
        if (parameterSets != null) beforeDecoderLock()
        var schedule = false
        val delivery = synchronized(lock) {
            // The output is read here, under the lock that setOutput takes, not where the frame was
            // let through: a withdrawal then either came first (nothing is made or fed) or comes after
            // (and abandons or releases what is made or fed here). Overtaken by a stop: nothing either.
            val target = output
            if (target == null || current !== stream) return@synchronized Delivery.NO_DECODER
            val inUse = decoder
            val needed = parameterSets != null && (inUse == null || !inUse.configuredWith.contentEquals(parameterSets.configuredWith))
            if (!needed) {
                val pending = wanted
                return@synchronized when {
                    // The owner dropped frames this one may depend on, and the reader has not seen it yet.
                    parameterSets == null && stream.ownerBrokeChain != null -> Delivery.REFUSED
                    inUse != null -> if (submitLocked(stream, inUse, payload, offset, length, timestampMicros, keyFrame)) Delivery.SUBMITTED else Delivery.REFUSED
                    pending != null -> waitLocked(pending, payload, offset, length, timestampMicros, keyFrame)
                    else -> Delivery.NO_DECODER
                }
            }

            val sets: ParameterSets = parameterSets
            val pending = wanted
            val creation = if (pending != null && pending.configuredWith.contentEquals(sets.configuredWith)) {
                pending
            } else {
                retireDecoderLocked("parameter sets changed")
                pending?.let { abandonLocked(it, "superseded by a keyframe with other parameter sets") }
                Creation(stream, target, outputGeneration, sets.parameters, sets.sps, sets.pps).also {
                    wanted = it
                    schedule = true
                }
            }
            waitLocked(creation, payload, offset, length, timestampMicros, keyFrame)
        }
        if (schedule) scheduleOwner()
        return delivery
    }

    /**
     * Submits one frame to the decoder in use. A keyframe the decoder takes by dropping pictures that
     * waited in it (D61) moves those from submitted to discarded.
     */
    private fun submitLocked(stream: Stream, target: FrameDecoder, data: ByteArray, offset: Int, length: Int, timestampMicros: Long, keyFrame: Boolean): Boolean {
        if (!target.submit(data, offset, length, timestampMicros, keyFrame)) return false
        if (keyFrame) {
            val superseded = target.takeSuperseded()
            if (superseded > 0) {
                stream.framesSubmitted.addAndGet(-superseded.toLong())
                stream.framesDiscarded.addAndGet(superseded.toLong())
            }
        }
        return true
    }

    /**
     * Keeps a copy of a frame for [creation]. When there would be too much, a keyframe replaces what
     * waits (D61); any other frame drops everything kept, and the chain waits for the next keyframe.
     */
    private fun waitLocked(creation: Creation, payload: ByteArray, offset: Int, length: Int, timestampMicros: Long, keyFrame: Boolean): Delivery {
        creation.arrived++
        val full = creation.waiting.size >= MAX_WAITING_FRAMES || creation.waitingBytes + length > MAX_WAITING_BYTES
        if (full && keyFrame && length <= MAX_WAITING_BYTES) {
            val dropped = creation.waiting.size
            discardWaitingLocked(creation)
            creation.stream.framesDiscarded.addAndGet(dropped.toLong())
            log("a keyframe replaced the $dropped frames waiting for the decoder being created")
        } else if (full) {
            val dropped = creation.waiting.size + 1
            discardWaitingLocked(creation)
            creation.stream.framesDiscarded.addAndGet(dropped.toLong())
            log("$dropped frames arrived while the decoder was being created, more than it may be given at once: dropped to the next keyframe")
            return Delivery.OVERFLOW
        }
        creation.waiting.add(Waiting(payload.copyOfRange(offset, offset + length), timestampMicros, keyFrame))
        creation.waitingBytes += length
        return Delivery.WAITING
    }

    private fun parameterSets(payload: ByteArray, offset: Int, length: Int): ParameterSets {
        val units = AnnexB.findNalUnits(payload, offset, offset + length)
        val sps = units.firstOrNull { it.type == NalType.SPS }?.let { payload.copyOfRange(it.payload, it.end) }
        val pps = units.firstOrNull { it.type == NalType.PPS }?.let { payload.copyOfRange(it.payload, it.end) }
        if (sps == null || pps == null) {
            throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "A frame flagged CONFIG does not contain an SPS and a PPS.")
        }
        val parameters = try {
            SequenceParameterSet.parse(sps)
        } catch (malformed: MalformedStreamException) {
            throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "The stream's sequence parameter set could not be read: ${malformed.message}")
        }
        return ParameterSets(parameters, sps, pps)
    }

    private fun breakChain(stream: Stream, reason: String, nowMicros: Long) {
        stream.gate.breakChain()
        stream.chainBreakReason = reason
        askForKeyframe(stream, nowMicros)
    }

    /** Once per recovery; again only if no keyframe has come [RE_ASK_MICROS] after the last request. */
    private fun askForKeyframe(stream: Stream, nowMicros: Long) {
        val asked = stream.keyframeAskedMicros
        if (asked != null && nowMicros - asked < RE_ASK_MICROS) return
        if (asked != null) log("no keyframe ${(nowMicros - asked) / 1_000} ms after the last request: asking again (${stream.chainBreakReason})")
        stream.keyframeAskedMicros = nowMicros
        stream.onKeyframeNeeded(stream.chainBreakReason)
    }

    // ---- Under the lock ------------------------------------------------------------------------

    /** Takes the decoder in use out of use, for the owner to release. True if there was one; the caller then schedules the owner. */
    private fun retireDecoderLocked(reason: String): Boolean {
        val retired = decoder ?: return false
        decoder = null
        decoderCreation = null
        toRelease.add(Retired(retired, reason))
        return true
    }

    /** No longer wants [creation]: what it makes will be released, and its frames are dropped. */
    private fun abandonLocked(creation: Creation, reason: String) {
        if (wanted === creation) wanted = null
        val dropped = creation.waiting.size
        creation.stream.framesDiscarded.addAndGet(dropped.toLong())
        discardWaitingLocked(creation)
        log("decoder creation abandoned ($reason)${if (creation.started) " while in progress" else " before it began"}; $dropped waiting frames dropped")
    }

    private fun discardWaitingLocked(creation: Creation) {
        creation.waiting.clear()
        creation.waitingBytes = 0
    }

    // ---- The decoder owner ---------------------------------------------------------------------

    private fun scheduleOwner() {
        synchronized(lock) {
            if (ownerScheduled) return
            ownerScheduled = true
        }
        owner.execute(::runOwner)
    }

    /**
     * The owner's one task: releases what was taken out of use, then creates the decoder wanted, if
     * any, until nothing is left; then tells whoever waits that everything before them is done.
     * Releases come first, so a decoder is gone from an output before another is made on it.
     */
    private fun runOwner() {
        while (true) {
            var releases: List<Retired> = emptyList()
            var creation: Creation? = null
            var idle: List<CompletableDeferred<Unit>> = emptyList()
            synchronized(lock) {
                releases = toRelease.toList()
                toRelease.clear()
                creation = wanted?.takeIf { !it.started }?.also { it.started = true }
                if (releases.isEmpty() && creation == null) {
                    ownerScheduled = false
                    idle = ownerIdle.toList()
                    ownerIdle.clear()
                }
            }
            if (releases.isEmpty() && creation == null) {
                idle.forEach { it.complete(Unit) }
                return
            }
            releases.forEach { release(it.decoder, it.reason) }
            creation?.let(::create)
        }
    }

    /** On the owner: makes the decoder [creation] asks for, and publishes it only if it is still wanted. */
    private fun create(creation: Creation) {
        val stream = creation.stream
        val made = try {
            platform.create(creation.output, creation.parameters, creation.sps, creation.pps, stream.choice, creation)
        } catch (exception: Exception) {
            val stillWanted = synchronized(lock) {
                (wanted === creation).also { if (it) abandonLocked(creation, "it could not be created") }
            }
            val description = "The decoder ${stream.choice.name} could not be started for ${creation.parameters.width} x ${creation.parameters.height}: ${exception.message}"
            if (stillWanted) {
                val fault = decoderFault(CODE_DECODER_START_FAILED, description)
                log(fault.message)
                stream.fault.complete(fault)
            } else {
                // Its stream or output had already gone (a surface torn down mid-creation fails like
                // this): not this session's fault, and not a newer stream's.
                log("$description; no longer wanted, ignored")
            }
            return
        }

        var waited = 0
        var refused = 0
        val published = synchronized(lock) {
            if (wanted !== creation || current !== stream || output !== creation.output || outputGeneration != creation.outputGeneration) {
                return@synchronized false
            }
            wanted = null
            decoder = made
            decoderCreation = creation
            // In order, before any later frame: the reader delivers under this lock too.
            while (true) {
                val next = creation.waiting.pollFirst() ?: break
                if (submitLocked(stream, made, next.data, 0, next.data.size, next.timestampMicros, next.keyFrame)) {
                    stream.framesSubmitted.incrementAndGet()
                    waited++
                } else {
                    refused = 1 + creation.waiting.size
                    stream.framesDiscarded.addAndGet(refused.toLong())
                    discardWaitingLocked(creation)
                    stream.ownerBrokeChain = REASON_DECODER_BACKLOG
                }
            }
            creation.waitingBytes = 0
            true
        }
        if (!published) {
            release(made, "created for a stream or an output no longer current")
            return
        }
        stream.decodersCreated.incrementAndGet()
        log(
            "decoder ${stream.choice.name} started for ${creation.parameters.width} x ${creation.parameters.height}, ${creation.parameters.profileName} profile, " +
                "level ${creation.parameters.levelName}; low latency ${made.lowLatency}; ready ${(nowMicros() - creation.requestedMicros) / 1_000} ms after it was asked for, " +
                "while ${creation.arrived} frames arrived; $waited waiting frames submitted" + if (refused > 0) ", $refused refused" else "",
        )
    }

    /** Releases a decoder its caller holds alone. A failure is logged: there is nothing else to do with it. */
    private fun release(released: FrameDecoder, reason: String) {
        val startedMicros = nowMicros()
        try {
            released.release()
        } catch (exception: Exception) {
            log("releasing a decoder ($reason) failed: ${exception.javaClass.simpleName}: ${exception.message}")
            return
        }
        log("decoder released ($reason) in ${(nowMicros() - startedMicros) / 1_000} ms")
    }

    private fun summary(stream: Stream): String =
        "received ${stream.framesReceived.get()} (keyframes ${stream.keyframesReceived.get()}), missing ${stream.gate.framesMissing}, " +
            "discarded ${stream.framesDiscarded.get()}, submitted ${stream.framesSubmitted.get()}, rendered ${stream.framesRendered.get()}, " +
            "decoders ${stream.decodersCreated.get()}, decoder errors ${stream.decoderErrors.get()}"

    internal companion object {
        private const val CODEC_H264 = "h264"
        private const val CODE_CODEC_UNSUPPORTED = "codec_unsupported"
        private const val CODE_DECODER_UNAVAILABLE = "decoder_unavailable"
        private const val CODE_DECODER_START_FAILED = "decoder_start_failed"

        private const val RANDOM_ACCESS = Protocol.FLAG_KEYFRAME or Protocol.FLAG_CONFIG

        /**
         * Frames kept for a decoder being created: as many as a new decoder takes before its first
         * output (SurfaceDecoder.MAX_PENDING_AT_STARTUP), half a second at 60 fps, which covers the
         * slowest creation measured (377 ms).
         */
        const val MAX_WAITING_FRAMES = SurfaceDecoder.MAX_PENDING_AT_STARTUP

        /** And at most this many bytes of them: one video payload of the largest size the protocol allows. */
        const val MAX_WAITING_BYTES = Protocol.MAX_VIDEO_PAYLOAD.toLong()

        const val OWNER_THREAD_NAME = "extendspace-decoder-owner"

        /**
         * How long a recovery waits for its keyframe before asking again (D61). The host passes on
         * one request per 500 ms and defers, never drops, the rest (protocol section 10), so a
         * keyframe normally follows within that; this covers a request lost with its connection or a
         * host that does not answer.
         */
        const val RE_ASK_MICROS = 1_000_000L

        // Protocol section 10 reasons.
        private const val REASON_STARTUP = "startup"
        private const val REASON_SEQUENCE_GAP = "sequence_gap"
        private const val REASON_DECODE_ERROR = "decode_error"
        private const val REASON_SURFACE_RECREATED = "surface_recreated"
        private const val REASON_DECODER_BACKLOG = "decoder_backlog"

        /**
         * The real decoder owner: one thread, one task at a time, in order. The thread ends after
         * [idleMillis] with nothing to do and is started again when there is.
         */
        fun decoderOwner(idleMillis: Long = 10_000): Executor =
            ThreadPoolExecutor(1, 1, idleMillis, TimeUnit.MILLISECONDS, LinkedBlockingQueue()) { task ->
                Thread(task, OWNER_THREAD_NAME).apply { isDaemon = true }
            }.apply { allowCoreThreadTimeOut(true) }

        private fun decoderFault(code: String, message: String) = SessionFault(FaultCategory.DECODER, code, message, FaultTreatment.TERMINAL)

        private fun readUnsignedInt(data: ByteArray, offset: Int): Long =
            ((data[offset].toLong() and 0xFF) shl 24) or
                ((data[offset + 1].toLong() and 0xFF) shl 16) or
                ((data[offset + 2].toLong() and 0xFF) shl 8) or
                (data[offset + 3].toLong() and 0xFF)

        private fun readLong(data: ByteArray, offset: Int): Long {
            var value = 0L
            for (i in 0 until 8) value = (value shl 8) or (data[offset + i].toLong() and 0xFF)
            return value
        }
    }
}
