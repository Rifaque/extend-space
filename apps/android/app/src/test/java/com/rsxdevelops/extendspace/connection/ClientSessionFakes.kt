package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue

// Scripted substitutes for the controller's seams. By default everything succeeds at once; a test
// replaces the behaviour it is about. Every call goes to one shared log, with the controller's
// state changes, so that tests can assert the order of things across components. With
// Dispatchers.Unconfined and the manual clock, the controller runs on the test's own thread.

internal class ClientLog {
    val entries = mutableListOf<String>()

    fun add(entry: String) {
        entries += entry
    }

    fun count(entry: String) = entries.count { it == entry }

    fun indexOf(entry: String) = entries.indexOf(entry)

    fun from(entry: String) = entries.dropWhile { it != entry }
}

internal object ClientFaults {
    val refusedPeer = SessionFault(FaultCategory.CONNECTION, "peer_refused", "A connection came from something other than adb.", FaultTreatment.RETRYABLE)
    val handshakeTimeout = SessionFault(FaultCategory.TIMEOUT, "handshake_timeout", "The PC said nothing for 5 s.", FaultTreatment.RETRYABLE)
    val hostClosed = SessionFault(FaultCategory.CONNECTION, "host_closed_connection", "The PC closed the connection.", FaultTreatment.RETRYABLE)
    val cannotListen = SessionFault(FaultCategory.TRANSPORT, "cannot_listen", "The local socket could not be listened on.", FaultTreatment.TERMINAL)
    val versionMismatch = SessionFault(FaultCategory.PROTOCOL, SessionFaultCodes.PROTOCOL_VERSION_UNSUPPORTED, "The PC speaks another version.", FaultTreatment.TERMINAL)
    val malformed = SessionFault(FaultCategory.PROTOCOL, "malformed_message", "The PC sent a malformed message.", FaultTreatment.TERMINAL)

    // Marked retryable on purpose: a decoder that cannot be had must still end the session.
    val noDecoder = SessionFault(FaultCategory.DECODER, "no_decoder", "No decoder can take 1920 x 1080.", FaultTreatment.RETRYABLE)
    val decoderLost = SessionFault(FaultCategory.DECODER, "decoder_create_failed", "The decoder could not be created for the new parameter sets.", FaultTreatment.TERMINAL)
}

internal class TestOutput(private val name: String) : VideoOutput {
    override fun toString() = name
}

/** Suspends until cancelled, writing [onCancel] to the log when it is: a blocked read whose cancellation closes its socket. */
internal suspend fun <T> blockedUntilCancelled(log: ClientLog, onCancel: String): T = suspendCancellableCoroutine { continuation ->
    continuation.invokeOnCancellation { log.add(onCancel) }
}

/** Suspends until [gate] completes and ignores cancellation meanwhile: a call that breaks its contract. */
internal suspend fun <T> stuckUntil(gate: CompletableDeferred<T>): T = withContext(NonCancellable) { gate.await() }

internal class FakeLink(private val log: ClientLog) : SessionLink {
    companion object {
        val stream = StreamDescription("h264", 1920, 1080, 60, "DESKTOP", "0.1.0")
    }

    private var end: CompletableDeferred<LinkEnd>? = null
    private var accepted = false

    var awaitHostBehaviour: suspend (attempt: Int) -> AttemptOutcome<Unit> = { AttemptOutcome.Succeeded(Unit) }
    var exchangeBehaviour: suspend (attempt: Int) -> AttemptOutcome<StreamDescription> = { AttemptOutcome.Succeeded(stream) }
    var acceptBehaviour: suspend (attempt: Int) -> AttemptOutcome<Unit> = { AttemptOutcome.Succeeded(Unit) }
    var endLinkBehaviour: suspend () -> Unit = {}

    var hostsAwaited = 0
        private set
    var exchanges = 0
        private set
    var accepts = 0
        private set
    var endLinks = 0
        private set
    var listening = false
        private set
    var listenStarts = 0
        private set
    var offeredDecoders: List<DecoderCapability> = emptyList()
        private set
    var lastEnd: Pair<String, SessionFault?>? = null
        private set
    var sink: VideoSink? = null
        private set

    /** Keyframe requests that reached the host: only while a stream is accepted. */
    val keyframesSent = mutableListOf<String>()

    override suspend fun awaitHost(): AttemptOutcome<Unit> {
        log.add("link.awaitHost")
        hostsAwaited++
        accepted = false
        if (!listening) {
            listening = true
            listenStarts++
        }
        return awaitHostBehaviour(hostsAwaited)
    }

    override suspend fun exchangeCapabilities(decoders: List<DecoderCapability>): AttemptOutcome<StreamDescription> {
        log.add("link.exchange")
        exchanges++
        offeredDecoders = decoders
        return exchangeBehaviour(exchanges)
    }

    override suspend fun acceptStream(decoder: DecoderChoice, video: VideoSink): AttemptOutcome<Unit> {
        log.add("link.accept ${decoder.name}")
        accepts++
        sink = video
        val outcome = acceptBehaviour(accepts)
        accepted = outcome is AttemptOutcome.Succeeded
        return outcome
    }

    override suspend fun awaitEnd(): LinkEnd {
        // The contract (gap G9): nothing to wait for before a stream is accepted.
        check(accepted) { "awaitEnd was called before a stream was accepted." }
        log.add("link.awaitEnd")
        val waiting = CompletableDeferred<LinkEnd>()
        end = waiting
        return waiting.await()
    }

    fun end(linkEnd: LinkEnd) {
        log.add("link ended: ${linkEnd.kind}")
        accepted = false
        end!!.complete(linkEnd)
    }

    override fun requestKeyframe(reason: String) {
        log.add("link.keyframe $reason")
        if (accepted) keyframesSent += reason
    }

    override suspend fun endLink(reason: String, fault: SessionFault?) {
        log.add("link.endLink")
        endLinks++
        lastEnd = reason to fault
        accepted = false
        end?.complete(LinkEnd(LinkEndKind.CLOSED))
        endLinkBehaviour()
    }

    override suspend fun stopListening() {
        log.add("link.stopListening")
        listening = false
    }
}

internal class FakeReceiver(private val log: ClientLog) : VideoReceiver {
    private var fault: CompletableDeferred<SessionFault?>? = null

    var prepareBehaviour: suspend (StreamDescription) -> AttemptOutcome<DecoderChoice> = { AttemptOutcome.Succeeded(DecoderChoice("c2.hw.avc", true)) }
    var stopBehaviour: suspend () -> Unit = {}

    var output: VideoOutput? = null
        private set
    var stream: StreamDescription? = null
        private set
    var starts = 0
        private set
    var stops = 0
        private set
    var onKeyframeNeeded: ((String) -> Unit)? = null
        private set

    override val sink = VideoSink { _, _, _ -> }

    override suspend fun decoders(): List<DecoderCapability> {
        log.add("receiver.decoders")
        return listOf(DecoderCapability("c2.hw.avc", true, true, 4096, 4096))
    }

    override suspend fun prepare(stream: StreamDescription): AttemptOutcome<DecoderChoice> {
        log.add("receiver.prepare")
        return prepareBehaviour(stream)
    }

    override fun start(stream: StreamDescription, decoder: DecoderChoice, onKeyframeNeeded: (reason: String) -> Unit) {
        log.add("receiver.start")
        starts++
        this.stream = stream
        this.onKeyframeNeeded = onKeyframeNeeded
    }

    override fun setOutput(output: VideoOutput?) {
        log.add("receiver.output ${output ?: "none"}")
        this.output = output
    }

    override suspend fun awaitFault(): SessionFault? {
        val waiting = CompletableDeferred<SessionFault?>()
        fault = waiting
        return waiting.await()
    }

    fun fail(failure: SessionFault) {
        log.add("receiver failed: ${failure.code}")
        fault!!.complete(failure)
    }

    override suspend fun stop() {
        log.add("receiver.stop")
        stops++
        stream = null
        fault?.complete(null)
        stopBehaviour()
    }
}

internal class ClientHarness(options: ClientSessionOptions = ClientSessionOptions()) {
    val log = ClientLog()
    val clock = ManualSessionClock()
    val link = FakeLink(log)
    val receiver = FakeReceiver(log)
    val states = mutableListOf<ConnectionState>()
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
    val controller = ClientSessionController(link, receiver, clock, scope, options) { state ->
        states += state
        log.add("state ${phaseOf(state)}")
    }

    val state: ConnectionState get() = controller.state.value
    val phase: String get() = phaseOf(state)
    val phases: List<String> get() = states.map(::phaseOf)

    fun start(): WaitResult = controller.waitForHost(TransportKind.USB)

    fun streaming(): ClientHarness {
        assertEquals(WaitResult.STARTED, start())
        assertEquals("Streaming", phase)
        return this
    }

    /**
     * What every test may require at its end: no clock delay left except the grace period of a
     * stream with no output, and nothing left listening or decoding after the session ended.
     */
    fun assertSettled() {
        val graceTimer = if (phase == "Streaming" && controller.output == null) 1 else 0
        assertEquals("pending clock delays", graceTimer, clock.pendingDelays)
        if (phase == "Disconnected" || phase == "Failed") {
            assertTrue("still listening after the session ended", !link.listening)
            assertTrue("a decoder stream is still running after the session ended", receiver.stream == null)
        }
    }

    companion object {
        fun phaseOf(state: ConnectionState): String = when (state) {
            ConnectionState.Disconnected -> "Disconnected"
            is ConnectionState.Connecting -> "Connecting"
            is ConnectionState.Negotiating -> "Negotiating"
            is ConnectionState.Streaming -> "Streaming"
            is ConnectionState.Reconnecting -> "Reconnecting"
            ConnectionState.Disconnecting -> "Disconnecting"
            is ConnectionState.Failed -> "Failed"
        }
    }
}
