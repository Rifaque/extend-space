package com.rsxdevelops.extendspace.link

import com.rsxdevelops.extendspace.connection.AttemptOutcome
import com.rsxdevelops.extendspace.connection.DecoderChoice
import com.rsxdevelops.extendspace.connection.FaultCategory
import com.rsxdevelops.extendspace.connection.FaultTreatment
import com.rsxdevelops.extendspace.connection.LinkEnd
import com.rsxdevelops.extendspace.connection.LinkEndKind
import com.rsxdevelops.extendspace.connection.SessionFault
import com.rsxdevelops.extendspace.connection.SessionFaultCodes
import com.rsxdevelops.extendspace.connection.SessionLink
import com.rsxdevelops.extendspace.connection.StreamDescription
import com.rsxdevelops.extendspace.connection.TransportKind
import com.rsxdevelops.extendspace.connection.VideoSink
import com.rsxdevelops.extendspace.protocol.Bye
import com.rsxdevelops.extendspace.protocol.ClientCapabilities
import com.rsxdevelops.extendspace.protocol.ClientStats
import com.rsxdevelops.extendspace.protocol.ControlCodec
import com.rsxdevelops.extendspace.protocol.ControlMessage
import com.rsxdevelops.extendspace.protocol.ErrorCode
import com.rsxdevelops.extendspace.protocol.ErrorMessage
import com.rsxdevelops.extendspace.protocol.FrameHeader
import com.rsxdevelops.extendspace.protocol.FrameParser
import com.rsxdevelops.extendspace.protocol.Hello
import com.rsxdevelops.extendspace.protocol.InputFrame
import com.rsxdevelops.extendspace.protocol.KeyframeRequest
import com.rsxdevelops.extendspace.protocol.Ping
import com.rsxdevelops.extendspace.protocol.Pong
import com.rsxdevelops.extendspace.protocol.Protocol
import com.rsxdevelops.extendspace.protocol.ProtocolException
import com.rsxdevelops.extendspace.protocol.StreamConfig
import com.rsxdevelops.extendspace.protocol.StreamReady
import com.rsxdevelops.extendspace.video.HostClockEstimate
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.io.EOFException
import java.io.IOException
import java.util.ArrayDeque
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import com.rsxdevelops.extendspace.connection.DecoderCapability as OfferedDecoder
import com.rsxdevelops.extendspace.protocol.DecoderCapability as WireDecoder

/** What the `capabilities` message says about this device's screen. */
internal data class DisplayInfo(val width: Int, val height: Int, val refreshHz: Double)

internal data class UsbHostLinkOptions(
    val handshakeStepMillis: Int = Protocol.HANDSHAKE_STEP_TIMEOUT_MS,
    val keepaliveMillis: Int = Protocol.KEEPALIVE_TIMEOUT_MS,
    val statsIntervalMillis: Long = Protocol.STATS_INTERVAL_MS,
    /** How long a goodbye or a fatal error may take to write before the connection is closed regardless. */
    val lastWordsMillis: Long = 500,
    /** How long closing waits for each of the connection's threads. */
    val joinMillis: Long = 1_000,
    val writeQueueCapacity: Int = 16,
    /** Who may connect: adb's daemon runs as the shell user (2000), or root (0). Null admits anyone, for tests. */
    val allowedPeerUids: Set<Int>? = setOf(UID_ROOT, UID_SHELL),
) {
    private companion object {
        // Android's fixed user IDs for root and the adb shell user (android_filesystem_config.h).
        const val UID_ROOT = 0
        const val UID_SHELL = 2000
    }
}

/**
 * The production [SessionLink] for USB: the socket half of the debug experiment's `LiveSession`
 * (accept, peer check, handshake, reader) behind the session contract, with **one writer thread**
 * and a small bounded queue in place of the experiment's four writers sharing a lock (limitation
 * L4, decision D15). The protocol code is the experiment's, copied to `main` unchanged.
 *
 * Listening is separate from connections. The first [awaitHost] starts an accept thread on the local
 * socket, which runs until [stopListening]; each accepted connection whose peer is adb (the shell
 * user, or root) is handed to the next [awaitHost], and one still waiting is replaced by a newer one.
 *
 * Handshake steps block on the socket, each on a thread of its own, bounded by the protocol's 5 s per
 * step; cancelling one closes the socket, which ends the read. From [acceptStream] the connection has
 * a reader thread (video straight to the sink, pings answered, the end detected) and a writer thread
 * (pongs, statistics once a second, keyframe requests, last words). Nothing here blocks the caller.
 */
internal class UsbHostLink(
    private val listenerFactory: HostListenerFactory,
    private val deviceName: String,
    private val appVersion: () -> String,
    private val display: () -> DisplayInfo,
    /** This device's counters for the `stats` message; null while there is nothing to report. */
    private val stats: () -> ClientStats?,
    private val hostClock: HostClockEstimate,
    private val options: UsbHostLinkOptions = UsbHostLinkOptions(),
    private val log: (String) -> Unit = {},
    private val nowMicros: () -> Long = { System.nanoTime() / 1_000 },
) : SessionLink {
    private val lock = Any()
    private val arrivals = Channel<Unit>(Channel.CONFLATED)

    // Under [lock].
    private var listener: HostListener? = null
    private var acceptThread: Thread? = null
    private var waiting: HostConnection? = null
    private var listenerLost: String? = null
    private var current: Connection? = null

    @Volatile var refusedPeers = 0
        private set

    override val connectedTransport: TransportKind?
        get() = synchronized(lock) { current?.socket?.transport }

    // ---- Listening ---------------------------------------------------------------------------

    override suspend fun awaitHost(): AttemptOutcome<Unit> {
        discardCurrent()
        val listening = synchronized(lock) { listener }
        if (listening == null) {
            val started = try {
                withContext(Dispatchers.IO) { listenerFactory.listen() }
            } catch (exception: IOException) {
                return AttemptOutcome.Failed(
                    SessionFault(
                        FaultCategory.TRANSPORT,
                        CODE_LISTEN_FAILED,
                        "This device could not listen for the PC on '${Protocol.LOCAL_SOCKET_NAME}': ${exception.message}. Another screen of the app may be using it.",
                        FaultTreatment.TERMINAL,
                        userActionable = true,
                    ),
                )
            }
            synchronized(lock) {
                listener = started
                listenerLost = null
                acceptThread = Thread({ acceptLoop(started) }, "extendspace-link-accept").apply {
                    isDaemon = true
                    start()
                }
            }
            log("listening for the PC")
        }

        while (true) {
            synchronized(lock) {
                waiting?.let { accepted ->
                    waiting = null
                    accepted.setReadTimeout(options.handshakeStepMillis)
                    current = Connection(accepted)
                    return AttemptOutcome.Succeeded(Unit)
                }
                listenerLost?.let { reason ->
                    listener = null
                    listenerLost = null
                    return AttemptOutcome.Failed(SessionFault(FaultCategory.TRANSPORT, CODE_LISTENER_LOST, reason, FaultTreatment.RETRYABLE))
                }
            }
            arrivals.receive()
        }
    }

    override suspend fun stopListening() {
        val (stopped, thread, pending) = synchronized(lock) {
            Triple(listener, acceptThread, waiting).also {
                listener = null
                acceptThread = null
                waiting = null
            }
        }
        pending?.close()
        if (stopped == null) return
        withContext(Dispatchers.IO) {
            stopped.close()
            thread?.join(options.joinMillis)
        }
        log("stopped listening")
    }

    private fun acceptLoop(listening: HostListener) {
        while (true) {
            val accepted = try {
                listening.accept()
            } catch (exception: IOException) {
                synchronized(lock) {
                    if (listener === listening) listenerLost = "Listening for the PC stopped unexpectedly: ${exception.message}"
                }
                arrivals.trySend(Unit)
                return
            }

            // The socket is in the abstract namespace, where any app on the device could connect.
            // Only adb's daemon is who this is for. A Wi-Fi connection was authenticated by its
            // listener (mutual TLS and the trust list, D76) and has no user ID to check.
            val uid = accepted.peerUid
            val allowed = options.allowedPeerUids
            if (!accepted.preauthorized && allowed != null && (uid == null || uid !in allowed)) {
                accepted.close()
                refusedPeers++
                log("refused a connection from uid $uid; only adb may connect")
                continue
            }

            val replaced = synchronized(lock) {
                if (listener !== listening) {
                    accepted.close()
                    return
                }
                waiting.also { waiting = accepted }
            }
            replaced?.close()
            arrivals.trySend(Unit)
        }
    }

    // ---- The handshake -------------------------------------------------------------------------

    override suspend fun exchangeCapabilities(decoders: List<OfferedDecoder>): AttemptOutcome<StreamDescription> {
        val connection = synchronized(lock) { current } ?: error("No host is connected: awaitHost has not succeeded.")
        return step(connection) {
            connection.writeNow(Protocol.PREFACE)
            connection.prefaceSent = true
            connection.writeNow(ControlCodec.encode(Hello(Protocol.MAJOR_VERSION, Protocol.MAJOR_VERSION, ROLE_CLIENT, deviceName, appVersion())))

            val hello = connection.expect<Hello>()
            if (hello.protocolMin > Protocol.MAJOR_VERSION || hello.protocolMax < Protocol.MAJOR_VERSION) {
                throw ProtocolException(
                    ErrorCode.PROTOCOL_VERSION_UNSUPPORTED,
                    "The PC speaks protocol versions ${hello.protocolMin} to ${hello.protocolMax}; this app speaks ${Protocol.MAJOR_VERSION}.",
                )
            }
            if (hello.role != ROLE_HOST) throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "The peer said it is a '${hello.role}', not a host.")

            val screen = display()
            connection.writeNow(
                ControlCodec.encode(
                    ClientCapabilities(
                        displayWidth = screen.width,
                        displayHeight = screen.height,
                        refreshHz = screen.refreshHz,
                        decoders = decoders.map { WireDecoder(it.name, it.hardware, it.lowLatency, it.maxWidth, it.maxHeight) },
                    ),
                ),
            )

            val config = connection.expect<StreamConfig>()
            connection.inputOffered = config.inputTouch
            log("host ${hello.deviceName} (${hello.appVersion}) offers ${config.codec} ${config.profile} ${config.width} x ${config.height} at ${config.frameRate} fps, ${config.bitrate} bit/s, encoder ${config.encoder} (${config.encoderAcceleration})")
            StreamDescription(config.codec, config.width, config.height, config.frameRate, hello.deviceName, hello.appVersion, config.inputTouch, config.mode == "extend")
        }
    }

    override suspend fun acceptStream(decoder: DecoderChoice, video: VideoSink): AttemptOutcome<Unit> {
        val connection = synchronized(lock) { current } ?: error("No host is connected: awaitHost has not succeeded.")
        val ready = step(connection) { connection.writeNow(ControlCodec.encode(StreamReady(decoder.name, decoder.hardware))) }
        if (ready !is AttemptOutcome.Succeeded) return ready
        connection.startStreaming(video)
        return ready
    }

    /** Runs one blocking handshake step on a thread of its own. Cancellation closes the socket, which ends it. */
    private suspend fun <T> step(connection: Connection, body: () -> T): AttemptOutcome<T> {
        try {
            return AttemptOutcome.Succeeded(
                suspendCancellableCoroutine { continuation ->
                    val worker = Thread(
                        {
                            try {
                                continuation.resume(body())
                            } catch (failure: Throwable) {
                                continuation.resumeWithException(failure)
                            }
                        },
                        "extendspace-link-handshake",
                    )
                    worker.isDaemon = true
                    continuation.invokeOnCancellation {
                        connection.closing = true
                        connection.socket.close()
                    }
                    worker.start()
                },
            )
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Throwable) {
            // A failed step leaves nothing behind: what can be said is said, then the socket is closed.
            val fault = handshakeFault(connection, failure)
            connection.closing = true
            connection.socket.close()
            log("handshake failed: ${fault.code}: ${fault.message}")
            return AttemptOutcome.Failed(fault)
        }
    }

    private fun handshakeFault(connection: Connection, failure: Throwable): SessionFault = when (failure) {
        is ProtocolException -> {
            connection.tryLastWords(ErrorMessage(failure.code, failure.message.orEmpty(), fatal = true))
            SessionFault(FaultCategory.PROTOCOL, failure.code, failure.message.orEmpty(), FaultTreatment.TERMINAL)
        }
        is HostEnded -> SessionFault(FaultCategory.CONNECTION, CODE_HOST_ENDED_HANDSHAKE, "The PC ended the session during the handshake: ${failure.reason}", FaultTreatment.RETRYABLE)
        is HostError -> when (failure.error.code) {
            WIRE_BUSY -> SessionFault(FaultCategory.CONNECTION, CODE_HOST_BUSY, failure.error.message, FaultTreatment.RETRYABLE)
            WIRE_TIMEOUT -> SessionFault(FaultCategory.TIMEOUT, CODE_HOST_TIMEOUT, failure.error.message, FaultTreatment.RETRYABLE)
            else -> SessionFault(FaultCategory.PROTOCOL, failure.error.code, failure.error.message, FaultTreatment.TERMINAL)
        }
        is EOFException -> SessionFault(FaultCategory.CONNECTION, CODE_HOST_CLOSED, "The PC closed the connection during the handshake.", FaultTreatment.RETRYABLE)
        is IOException ->
            if (isReadTimeout(connection.quietMillis(), options.handshakeStepMillis)) {
                connection.tryLastWords(ErrorMessage(WIRE_TIMEOUT, "Nothing arrived from the PC for ${options.handshakeStepMillis / 1000} s.", fatal = true))
                SessionFault(FaultCategory.TIMEOUT, CODE_HANDSHAKE_TIMEOUT, "The PC did not answer within ${options.handshakeStepMillis / 1000} s during the handshake.", FaultTreatment.RETRYABLE)
            } else {
                SessionFault(FaultCategory.CONNECTION, CODE_CONNECTION_FAILED, "The connection to the PC failed during the handshake: ${failure.message}", FaultTreatment.RETRYABLE)
            }
        else -> SessionFault.unexpected(failure)
    }

    // ---- Streaming -----------------------------------------------------------------------------

    override suspend fun awaitEnd(): LinkEnd {
        val connection = synchronized(lock) { current }
        check(connection != null && connection.streaming) { "No stream has been accepted on the current connection." }
        return connection.ended.await()
    }

    override fun sendInput(frame: InputFrame): Boolean {
        val connection = synchronized(lock) { current } ?: return false
        if (!connection.streaming || connection.closing || !connection.inputOffered) return false
        // Same writer and queue as pongs and statistics: input goes the opposite way to video, so
        // nothing large is ever ahead of it (research section 11). Not merged and not batched.
        connection.enqueue(frame)
        return true
    }

    override fun requestKeyframe(reason: String) {
        val connection = synchronized(lock) { current } ?: return
        if (!connection.streaming || connection.closing) return
        // Never dropped (D61): the host rate-limits requests and defers, rather than loses, one that
        // comes too soon (protocol section 10). One not yet written takes in any that follow it.
        if (!connection.enqueueKeyframeRequest(KeyframeRequest(reason))) log("keyframe request ($reason) merged into one not yet sent")
    }

    override suspend fun endLink(reason: String, fault: SessionFault?) {
        val connection = synchronized(lock) { current.also { current = null } } ?: return
        val lastWords: ControlMessage = fault?.let { ErrorMessage(wireCode(it), it.message, fatal = true) } ?: Bye(reason)
        withContext(Dispatchers.IO) { connection.close(lastWords) }
    }

    private suspend fun discardCurrent() {
        val left = synchronized(lock) { current.also { current = null } } ?: return
        withContext(Dispatchers.IO) { left.close(lastWords = null) }
    }

    private fun wireCode(fault: SessionFault): String = when (fault.category) {
        FaultCategory.PROTOCOL -> fault.code
        // A decoder that cannot be had for the offered stream (D29).
        FaultCategory.DECODER -> SessionFaultCodes.STREAM_CONFIG_REJECTED
        FaultCategory.TIMEOUT -> WIRE_TIMEOUT
        else -> ErrorCode.INTERNAL_ERROR
    }

    /** The host said goodbye during the handshake. */
    private class HostEnded(val reason: String) : IOException(reason)

    /** The host sent an `error` during the handshake. */
    private class HostError(val error: ErrorMessage) : IOException(error.message)

    private class Received(val header: FrameHeader, val payload: ByteArray)

    /** One accepted connection, from [awaitHost] to its close. */
    private inner class Connection(val socket: HostConnection) {
        private val parser = FrameParser()
        private val buffer = ByteArray(READ_BUFFER_BYTES)
        private val handshakeFrames = ArrayDeque<Received>()
        private val queueLock = Object()
        private val outgoing = ArrayDeque<ControlMessage>()
        private var lastWords: ControlMessage? = null
        private var lastWordsWritten = false
        private var reader: Thread? = null
        private var writer: Thread? = null
        private var sink: VideoSink? = null

        @Volatile private var lastReadNanos = System.nanoTime()
        @Volatile var closing = false
        @Volatile var streaming = false
        @Volatile var prefaceSent = false

        /** This connection's `stream_config` offered input (D81): only then is any sent. */
        @Volatile var inputOffered = false
        val ended = CompletableDeferred<LinkEnd>()

        fun quietMillis(): Long = (System.nanoTime() - lastReadNanos) / 1_000_000

        /** For the handshake, on its step thread. */
        fun writeNow(bytes: ByteArray) {
            socket.output.write(bytes)
            socket.output.flush()
        }

        /** Handshake step: the next control message must be a [T]. Unknown control types are skipped. */
        inline fun <reified T : ControlMessage> expect(): T {
            while (true) {
                val next = handshakeFrames.pollFirst()
                if (next == null) {
                    // Every frame a read completes is kept: one read may carry more than one message.
                    readOnce { header, payload, length -> handshakeFrames.add(Received(header, payload.copyOf(length))) }
                    continue
                }

                if (next.header.channel != Protocol.CHANNEL_CONTROL) {
                    throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "Expected ${T::class.simpleName}, received a frame on channel ${next.header.channel}.")
                }
                when (val message = ControlCodec.decode(next.header.type, next.payload)) {
                    is T -> return message
                    is ErrorMessage -> if (message.fatal) throw HostError(message) else log("the PC reported ${message.code}: ${message.message}")
                    is Bye -> throw HostEnded(message.reason)
                    null -> Unit // A control type from a later protocol version: ignored, as the protocol requires.
                    else -> throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "Expected ${T::class.simpleName}, received ${message.javaClass.simpleName}.")
                }
            }
        }

        private fun readOnce(handler: (FrameHeader, ByteArray, Int) -> Unit) {
            val read = socket.input.read(buffer)
            if (read < 0) {
                parser.complete()
                throw EOFException("The PC closed the connection.")
            }
            lastReadNanos = System.nanoTime()
            parser.append(buffer, 0, read) { header, payload, length -> handler(header, payload, length) }
        }

        fun startStreaming(video: VideoSink) {
            sink = video
            socket.setReadTimeout(options.keepaliveMillis)
            streaming = true
            writer = Thread(::writeLoop, "extendspace-link-writer").apply {
                isDaemon = true
                start()
            }
            reader = Thread(::readLoop, "extendspace-link-reader").apply {
                isDaemon = true
                start()
            }
        }

        /** Queues a keyframe request unless one is already waiting to be written. False if it was merged into that one. */
        fun enqueueKeyframeRequest(request: KeyframeRequest): Boolean = synchronized(queueLock) {
            if (outgoing.any { it is KeyframeRequest }) return false
            enqueueLocked(request)
            true
        }

        /** Queues a message for the writer. When the queue is full, statistics go first; beyond that the newest is dropped. */
        fun enqueue(message: ControlMessage) {
            synchronized(queueLock) { enqueueLocked(message) }
        }

        private fun enqueueLocked(message: ControlMessage) {
            if (lastWords != null) return
            if (outgoing.size >= options.writeQueueCapacity) {
                val stale = outgoing.firstOrNull { it is ClientStats }
                if (stale != null) outgoing.remove(stale) else return
            }
            outgoing.add(message)
            queueLock.notifyAll()
        }

        private fun writeLoop() {
            var nextStats = System.nanoTime() + options.statsIntervalMillis * 1_000_000
            try {
                while (true) {
                    val message: ControlMessage? = synchronized(queueLock) {
                        while (outgoing.isEmpty() && lastWords == null && !closing) {
                            val waitMillis = (nextStats - System.nanoTime()) / 1_000_000
                            if (waitMillis <= 0) break
                            queueLock.wait(waitMillis)
                        }
                        lastWords?.let { return@synchronized it }
                        if (closing) return
                        outgoing.pollFirst()
                    }

                    if (message == null) {
                        nextStats = System.nanoTime() + options.statsIntervalMillis * 1_000_000
                        stats()?.let { writeNow(ControlCodec.encode(it)) }
                        continue
                    }

                    writeNow(if (message is InputFrame) message.bytes else ControlCodec.encode(message))
                    if (message === synchronized(queueLock) { lastWords }) {
                        synchronized(queueLock) {
                            lastWordsWritten = true
                            queueLock.notifyAll()
                        }
                        return
                    }
                }
            } catch (exception: IOException) {
                if (!closing) finish(lost(CODE_SEND_FAILED, "Writing to the PC failed: ${exception.message}"), lastWords = null)
            }
        }

        private fun readLoop() {
            var end: LinkEnd? = null
            var last: ControlMessage? = null
            try {
                // Anything that arrived with the handshake's last message is handled first.
                while (end == null) {
                    val early = handshakeFrames.pollFirst() ?: break
                    end = handle(early.header, early.payload, early.payload.size)
                }
                while (end == null) {
                    readOnce { header, payload, length -> if (end == null) end = handle(header, payload, length) }
                }
            } catch (violation: ProtocolException) {
                end = LinkEnd(LinkEndKind.FATAL, SessionFault(FaultCategory.PROTOCOL, violation.code, violation.message.orEmpty(), FaultTreatment.TERMINAL))
                last = ErrorMessage(violation.code, violation.message.orEmpty(), fatal = true)
            } catch (closed: EOFException) {
                end = if (closing) LinkEnd(LinkEndKind.CLOSED) else lost(CODE_HOST_CLOSED, "The PC closed the connection without saying goodbye.")
            } catch (failure: IOException) {
                end = when {
                    closing -> LinkEnd(LinkEndKind.CLOSED)
                    // A link that stays open and goes silent ends in a read timeout, whose own message is the OS's "Try again".
                    isReadTimeout(quietMillis(), options.keepaliveMillis) -> LinkEnd(
                        LinkEndKind.LOST,
                        SessionFault(FaultCategory.TIMEOUT, CODE_KEEPALIVE_TIMEOUT, "Nothing arrived from the PC for ${options.keepaliveMillis / 1000} s.", FaultTreatment.RETRYABLE),
                    )
                    else -> lost(CODE_CONNECTION_FAILED, "The connection to the PC failed: ${failure.message}")
                }
            } catch (failure: RuntimeException) {
                end = LinkEnd(LinkEndKind.FATAL, SessionFault.unexpected(failure))
                last = ErrorMessage(ErrorCode.INTERNAL_ERROR, "${failure.javaClass.simpleName}: ${failure.message}", fatal = true)
            }
            if (!closing) finish(end!!, last)
        }

        /** One frame while streaming. Returns how the link ends, or null to go on. */
        private fun handle(header: FrameHeader, payload: ByteArray, length: Int): LinkEnd? {
            when (header.channel) {
                Protocol.CHANNEL_VIDEO -> sink?.accept(header.flags, payload, length)
                Protocol.CHANNEL_CONTROL -> when (val message = ControlCodec.decode(header.type, payload, length)) {
                    is Ping -> {
                        // Answered before anything else is done with it, to keep the host's round trip short.
                        enqueue(Pong(message.id, message.hostTimeMicros, nowMicros()))
                        message.clockOffsetMicros?.let { hostClock.update(it, message.roundTripMicros) }
                    }
                    is Bye -> return LinkEnd(LinkEndKind.PEER_ENDED, peerReason = message.reason)
                    is ErrorMessage ->
                        if (message.fatal) {
                            return LinkEnd(LinkEndKind.FATAL, SessionFault(FaultCategory.PROTOCOL, message.code, "The PC reported a fatal error: ${message.message}", FaultTreatment.TERMINAL))
                        } else {
                            log("the PC reported ${message.code}: ${message.message}")
                        }
                    is StreamConfig -> throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "Changing the stream configuration mid-session is not implemented in this build.")
                    null -> Unit // A control type from a later protocol version: ignored, as the protocol requires.
                    else -> throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "The PC sent control type ${header.type}, which a host does not send while streaming.")
                }
                else -> throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "The PC sent a frame on channel ${header.channel}.")
            }
            return null
        }

        private fun lost(code: String, message: String) =
            LinkEnd(LinkEndKind.LOST, SessionFault(FaultCategory.CONNECTION, code, message, FaultTreatment.RETRYABLE))

        /** The link ended by itself (on the reader or writer thread): say what can be said, close, report. */
        private fun finish(end: LinkEnd, lastWords: ControlMessage?) {
            if (!ended.complete(end)) return
            closing = true
            log("link ended: ${end.kind}${end.fault?.let { " ${it.code}: ${it.message}" } ?: end.peerReason?.let { ": $it" }.orEmpty()}")
            if (lastWords != null) sayLastWords(lastWords) else wakeWriter()
            socket.close()
        }

        /** Closed by this side. Called once, off the caller's thread. */
        fun close(lastWords: ControlMessage?) {
            val wasClosing = closing
            closing = true
            if (!wasClosing && lastWords != null && prefaceSent) {
                if (writer != null) sayLastWords(lastWords) else tryLastWords(lastWords)
            } else {
                wakeWriter()
            }
            socket.close()
            reader?.takeIf { it !== Thread.currentThread() }?.join(options.joinMillis)
            writer?.takeIf { it !== Thread.currentThread() }?.join(options.joinMillis)
            ended.complete(LinkEnd(LinkEndKind.CLOSED))
        }

        /** Hands [message] to the writer as its last and waits, bounded, for it to be written. */
        private fun sayLastWords(message: ControlMessage) {
            val deadline = System.nanoTime() + options.lastWordsMillis * 1_000_000
            synchronized(queueLock) {
                lastWords = message
                outgoing.clear()
                queueLock.notifyAll()
                while (!lastWordsWritten) {
                    val waitMillis = (deadline - System.nanoTime()) / 1_000_000
                    if (waitMillis <= 0) break
                    queueLock.wait(waitMillis)
                }
            }
        }

        /** Before streaming there is no writer: the last words get a thread of their own and a bound. */
        fun tryLastWords(message: ControlMessage) {
            if (!prefaceSent) return
            val speaker = Thread({ runCatching { writeNow(ControlCodec.encode(message)) } }, "extendspace-link-last-words")
            speaker.isDaemon = true
            speaker.start()
            speaker.join(options.lastWordsMillis)
        }

        private fun wakeWriter() = synchronized(queueLock) { queueLock.notifyAll() }
    }

    private companion object {
        const val READ_BUFFER_BYTES = 64 * 1024
        const val ROLE_CLIENT = "client"
        const val ROLE_HOST = "host"

        // Protocol section 12 codes this link reads or sends besides those in ErrorCode.
        const val WIRE_BUSY = "busy"
        const val WIRE_TIMEOUT = "timeout"

        const val CODE_LISTEN_FAILED = "listen_failed"
        const val CODE_LISTENER_LOST = "listener_lost"
        const val CODE_HOST_ENDED_HANDSHAKE = "host_ended_handshake"
        const val CODE_HOST_BUSY = "host_busy"
        const val CODE_HOST_TIMEOUT = "host_timeout"
        const val CODE_HOST_CLOSED = "host_closed"
        const val CODE_HANDSHAKE_TIMEOUT = "handshake_timeout"
        const val CODE_KEEPALIVE_TIMEOUT = "keepalive_timeout"
        const val CODE_CONNECTION_FAILED = "connection_failed"
        const val CODE_SEND_FAILED = "send_failed"

        /**
         * How much earlier than its limit a read timeout may fire and still be recognised as one. The
         * socket's timeout is kept in kernel ticks (4 ms at 250 Hz) and can end a fraction of a tick
         * before the whole time has passed on the monotonic clock; without this margin such a timeout
         * was reported as `connection_failed` (stage 4.1). The host pings every second, so 50 ms of
         * slack cannot turn a live link into a timed-out one.
         */
        const val READ_TIMEOUT_EARLY_MILLIS = 50L

        /** Whether a read that failed after [quietMillis] of silence was the socket's read timeout of [limitMillis]. */
        fun isReadTimeout(quietMillis: Long, limitMillis: Int): Boolean = quietMillis >= limitMillis - READ_TIMEOUT_EARLY_MILLIS
    }
}
