package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.delay

// The three seams between the client's session controller and everything it directs. Nothing in
// the app implements the first two with a real socket or decoder yet; ClientSessionController is
// written and tested on a desktop JVM against substitutes. See
// docs/architecture/production-session-architecture.md, sections 7 and 15.
//
// The client's side of a session is not the host's mirrored: the host connects and the client
// waits to be connected to; the host produces video and the client consumes it. So the first two
// contracts differ in shape from the Windows ones, and the third does not.
//
// Rules every implementation of SessionLink and VideoReceiver follows:
//
// - One call is one attempt. Nothing behind a seam retries or decides that a session is over.
// - The suspending calls follow coroutine cancellation. A call that is cancelled, or fails, leaves
//   nothing of its own behind: an accepted connection it was handshaking on is closed before the
//   call returns or rethrows. Listening, which only stopListening ends, is the exception.
// - Cancellation must actually end a call that is blocked: a call waiting in accept() or a socket
//   read closes that socket when it is cancelled (for accept(), by connecting to it, as the
//   experiment's wakeAccept does). The controller waits, bounded, for a cancelled call to return
//   before it calls endLink, so a call that ignored cancellation would hold teardown until the
//   deadline.
// - None of them blocks the calling thread: blocking work runs on the component's own threads.
//   VideoReceiver.setOutput(null) is the one deliberate exception, explained on it.
// - The controller never runs two suspending calls on the same seam at once, except endLink, stop
//   and stopListening after a step has overrun the teardown deadline. setOutput and
//   requestKeyframe are the exceptions by design: they may be called on any thread while anything
//   else is running.

/** One H.264 decoder this device has, as offered to the host in the `capabilities` message. */
internal data class DecoderCapability(
    val name: String,
    val hardware: Boolean,
    val lowLatency: Boolean,
    val maxWidth: Int,
    val maxHeight: Int,
)

/** What the host said the stream is: its `hello` and its `stream_config`. */
internal data class StreamDescription(
    val codec: String,
    val width: Int,
    val height: Int,
    val frameRate: Int,
    val hostName: String,
    val hostVersion: String,
    /** The host accepts touch on this stream (D81). */
    val inputOffered: Boolean = false,
    /** The stream is a virtual monitor extending the PC's desktop (D90). */
    val extended: Boolean = false,
)

/** The decoder the receiver will use for a stream, named to the host in `stream_ready`. */
internal data class DecoderChoice(val name: String, val hardware: Boolean)

internal enum class LinkEndKind {
    /** Lost without a goodbye: closed, reset, or silent for the keepalive time. The host may come back. */
    LOST,

    /** The host said goodbye. The session is over. */
    PEER_ENDED,

    /** Ended by something waiting cannot cure: a protocol violation, a fatal error from the host. */
    FATAL,

    /** Closed by this side, through [SessionLink.endLink]. */
    CLOSED,
}

/**
 * Why a link whose stream had been accepted is over.
 *
 * @param fault set for [LinkEndKind.LOST] (retryable) and [LinkEndKind.FATAL] (terminal).
 * @param peerReason the host's own words, for [LinkEndKind.PEER_ENDED]. For logs only.
 */
internal data class LinkEnd(val kind: LinkEndKind, val fault: SessionFault? = null, val peerReason: String? = null)

/** Receives one video payload as it came off the wire. Called on the link's reader thread; must not wait. */
internal fun interface VideoSink {
    /** [payload] holds the video frame's payload in its first [length] bytes and is reused after the call returns. */
    fun accept(flags: Int, payload: ByteArray, length: Int)
}

/**
 * The link to the host, from the controller's side. Behind it are the listening socket, the check
 * of who connected, the handshake, the keepalive and one writer for everything sent; none of them
 * shows through.
 *
 * Listening and a connection are separate. Listening starts with the first [awaitHost] and lasts
 * until [stopListening]; a connection is accepted by [awaitHost] and lasts until it ends or
 * [endLink]. A connection attempt is three calls, in order: [awaitHost], [exchangeCapabilities],
 * [acceptStream]. The controller asks the receiver for a decoder between the second and the third.
 */
internal interface SessionLink {
    /** The route of the connection [awaitHost] last accepted, while it lasts; null when there is none. */
    val connectedTransport: TransportKind? get() = null

    /**
     * Listens, if not already listening, and waits for a host to connect and pass the peer check.
     * A connection that ended or was left over from a failed attempt is discarded first. Fails
     * terminally only if the socket cannot be listened on at all.
     */
    suspend fun awaitHost(): AttemptOutcome<Unit>

    /**
     * On the accepted connection: preface and `hello` both ways, `capabilities` out offering
     * [decoders], and the host's `stream_config` in. A version mismatch is a terminal PROTOCOL fault
     * (the host is told); silence is a retryable TIMEOUT fault.
     */
    suspend fun exchangeCapabilities(decoders: List<DecoderCapability>): AttemptOutcome<StreamDescription>

    /**
     * Answers `stream_ready` naming [decoder]. From the moment this is sent, video goes to [video],
     * which must therefore be ready before the call.
     */
    suspend fun acceptStream(decoder: DecoderChoice, video: VideoSink): AttemptOutcome<Unit>

    /**
     * Returns once, when the connection whose stream was accepted is over, saying why. Valid only
     * after [acceptStream] has succeeded on the current connection; before that there is nothing to
     * wait for and it throws [IllegalStateException]. Cancelling the wait does not end the link.
     */
    suspend fun awaitEnd(): LinkEnd

    /**
     * Asks the host for a keyframe. Any thread; never blocks; dropped if no stream is accepted.
     * [reason] is a protocol section 10 reason. While a stream is accepted no request is dropped:
     * one not yet written takes in any that follow it, and the host rate-limits without losing any
     * (D61). Callers keep to one request per recovery.
     */
    fun requestKeyframe(reason: String)

    /**
     * Queues one input frame for the host. Any thread; never blocks. False, and nothing is sent, unless
     * a stream is accepted whose configuration offered input.
     */
    fun sendInput(frame: com.rsxdevelops.extendspace.protocol.InputFrame): Boolean = false

    /**
     * Ends the current connection, if any, and goes on listening. If [fault] is given and the host
     * can still be told, the last message is a fatal `error` derived from it (for a decoder that
     * cannot be created, `stream_config_rejected`); otherwise a `bye` carrying [reason]. Either is
     * sent only if it can be sent within its own short bound; closing the socket then unblocks any
     * read or write in progress. Safe in any state and more than once.
     */
    suspend fun endLink(reason: String, fault: SessionFault? = null)

    /** Stops listening. Does not end a current connection; [endLink] does that. Safe in any state and more than once. */
    suspend fun stopListening()
}

/** Somewhere decoded video can be shown. The UI lends one to the session and takes it back; the session never owns it. */
interface VideoOutput

/**
 * The receive-and-decode pipeline, from the controller's side: video payloads go in, pictures come
 * out on whatever output it has been lent. It knows nothing of sockets or of session state.
 *
 * A stream is [start] to [stop]; there may be many in the life of one receiver. The output is
 * independent of streams: once lent it is kept, across streams, until it is taken back. A decoder
 * exists only while there is both a stream and an output.
 */
internal interface VideoReceiver {
    /** Where the link is to deliver video. Discards everything while no stream is started. */
    val sink: VideoSink

    /** The H.264 decoders this device has, for the `capabilities` message. */
    suspend fun decoders(): List<DecoderCapability>

    /**
     * Chooses the decoder for [stream] without creating it: a decoder needs an output, which may not
     * be lent yet. Fails with a terminal DECODER fault if no decoder on this device can take it.
     */
    suspend fun prepare(stream: StreamDescription): AttemptOutcome<DecoderChoice>

    /**
     * Starts accepting video for [stream] with [decoder]. [onKeyframeNeeded] is called, on any
     * thread, with a protocol section 10 reason, when decoding cannot go on without a keyframe: a
     * sequence gap, a decoder error, a backlog. It is not called for an output being lent or for the
     * start of a stream; asking then is the controller's job.
     */
    fun start(stream: StreamDescription, decoder: DecoderChoice, onKeyframeNeeded: (reason: String) -> Unit)

    /**
     * Lends an output, or with null takes it back. Called on the UI thread, possibly while another
     * call of the receiver is running on the controller's thread. Taking it back returns
     * only once nothing is drawing to it any more, because the caller is about to destroy it: it
     * releases the decoder, bounded by the implementation, and waits for nothing else, never on the
     * network. Lending one output while another is lent replaces it the same way.
     */
    fun setOutput(output: VideoOutput?)

    /**
     * Returns with the fault if the running stream fails in a way the receiver cannot recover from
     * by itself (a decoder that cannot be created for the stream's parameter sets), and with null
     * when the stream is stopped. A decoder that fails while running is not such a fault: it is
     * replaced at the next keyframe. Cancelling the wait does not stop the stream.
     */
    suspend fun awaitFault(): SessionFault?

    /** Stops the stream and releases its decoder. Keeps the output. Safe in any state and more than once. */
    suspend fun stop()
}

/**
 * Time, as the controller sees it. Every wait the controller makes (the grace period without an
 * output, the wait for a lost host, teardown bounds) goes through this, so that its tests can move
 * time by hand.
 */
internal interface SessionClock {
    /** Monotonic milliseconds since an arbitrary start. Only differences mean anything. */
    fun elapsedMillis(): Long

    /** Returns after [millis] of this clock's time, or is cancelled with the calling coroutine. */
    suspend fun delay(millis: Long)
}

/** The real clock. */
internal class SystemSessionClock : SessionClock {
    private val start = System.nanoTime()

    override fun elapsedMillis(): Long = (System.nanoTime() - start) / 1_000_000

    override suspend fun delay(millis: Long) = delay(timeMillis = millis)
}
