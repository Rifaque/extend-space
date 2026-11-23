package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicInteger

internal data class ClientSessionOptions(
    /** Between a failed attempt and listening for the next host. Caps how fast a misbehaving host can make attempts. */
    val retryPauseMillis: Long = 250,

    /** How long a host that was lost while streaming has to come back (decision D6, provisional). */
    val hostReturnMillis: Long = 30_000,

    /** How long a streaming session may have no output before it ends itself (decision D6, provisional). */
    val outputGraceMillis: Long = 30_000,

    /** The whole of teardown (decision D26, provisional; device test V7). */
    val teardownDeadlineMillis: Long = 4_000,
)

/**
 * The one owner of this device's session (architecture sections 4 and 7.3), held at application
 * scope. It decides; the link and the receiver do. Once a session exists it is the only thing
 * that dispatches into the state machine.
 *
 * **Concurrency.** Decisions run on [scope], which must run one coroutine at a time (a dispatcher
 * limited to one thread in the app; `Dispatchers.Unconfined` with fakes in tests, so that a test's
 * thread is the controller's thread). The intents ([waitForHost], [stop], [acknowledgeError])
 * return at once from any thread: they compare-and-set the state machine and hand over to [scope].
 * [lendOutput] and [withdrawOutput] are for the UI thread; withdrawing waits for the receiver to
 * stop drawing, and for nothing else.
 *
 * **A session** waits for a host, negotiates (capabilities, decoder choice, ready), streams, and
 * goes back to waiting when the link is lost, for at most [ClientSessionOptions.hostReturnMillis].
 * Before it first streams it waits for a host indefinitely: the user asked it to. It never retries
 * anything itself beyond waiting for the next host; there is no backoff, only a short pause.
 *
 * **Teardown**: cancel the session's work and wait for it, bounded; end the link (goodbye or fatal
 * error, then close); stop the receiver; stop listening; then `TeardownCompleted` or `Failed`,
 * last. One deadline bounds all of it. The lent output is never touched: it is the UI's.
 */
internal class ClientSessionController(
    private val link: SessionLink,
    private val receiver: VideoReceiver,
    private val clock: SessionClock,
    private val scope: CoroutineScope,
    private val options: ClientSessionOptions = ClientSessionOptions(),
    /** Called after every accepted transition with the new state, on the thread that made it. For diagnostics and tests. */
    private val onStateChanged: (ConnectionState) -> Unit = {},
) : ConnectionController {
    private val machine = ConnectionStateMachine()
    private val intentLock = Any()
    private val unreleased = AtomicInteger()
    private val outputs = MutableStateFlow<VideoOutput?>(null)
    private val currentStream = MutableStateFlow<StreamInfo?>(null)

    @Volatile private var streaming = false
    @Volatile private var lastTeardownFaults: List<SessionFault> = emptyList()

    // Touched only on [scope].
    private var run: Run? = null

    override val state: StateFlow<ConnectionState> = machine.state
    override val hosts: StateFlow<List<HostDevice>> = MutableStateFlow<List<HostDevice>>(emptyList()).asStateFlow()
    override val stream: StateFlow<StreamInfo?> = currentStream.asStateFlow()

    private val connectedOverFlow = MutableStateFlow<TransportKind?>(null)
    override val connectedOver: StateFlow<TransportKind?> = connectedOverFlow.asStateFlow()

    /** False while something a teardown abandoned has not returned yet. [waitForHost] is refused meanwhile. */
    val isReleased: Boolean get() = unreleased.get() == 0

    /** Cleanup faults of the most recent teardown: steps that threw or overran. */
    val teardownFaults: List<SessionFault> get() = lastTeardownFaults

    /** The output currently lent, if any. */
    val output: VideoOutput? get() = outputs.value

    override fun waitForHost(transport: TransportKind): WaitResult {
        synchronized(intentLock) {
            if (!isReleased) return WaitResult.PREVIOUS_SESSION_NOT_RELEASED

            // The state machine's compare-and-set is the one duplicate-start guard.
            if (!apply(ConnectionEvent.ConnectRequested(transport))) return WaitResult.ALREADY_ACTIVE
            scope.launch { beginRun() }
        }
        return WaitResult.STARTED
    }

    override fun stop(): Boolean {
        synchronized(intentLock) {
            if (!apply(ConnectionEvent.DisconnectRequested)) return false
            scope.launch { run?.let { if (!it.ending) requestTeardown(it, Ending.stop(STOPPED_ON_DEVICE)) } }
        }
        return true
    }

    override fun acknowledgeError(): Boolean = synchronized(intentLock) { apply(ConnectionEvent.ErrorAcknowledged) }

    private fun apply(event: ConnectionEvent): Boolean {
        if (!machine.dispatch(event)) return false
        val now = machine.state.value
        // Which route the PC came by, while it is connected (USB, or Wi-Fi since D74).
        connectedOverFlow.value = if (now is ConnectionState.Negotiating || now is ConnectionState.Streaming) link.connectedTransport else null
        onStateChanged(now)
        return true
    }

    override fun sendTouch(sequence: Long, contacts: List<TouchContactSample>): Boolean {
        if (!streaming || contacts.isEmpty()) return false
        return link.sendInput(
            com.rsxdevelops.extendspace.protocol.InputFrames.touchFrame(
                sequence,
                contacts.map { com.rsxdevelops.extendspace.protocol.TouchContact(it.id, it.phase, it.x, it.y) },
            ),
        )
    }

    override fun lendOutput(output: VideoOutput) {
        receiver.setOutput(output)
        outputs.value = output

        // Written then read in the opposite order to the session's (it sets streaming, then reads
        // the output), so that at least one of the two asks when an output and a stream meet.
        if (streaming) link.requestKeyframe(REASON_SURFACE_RECREATED)
    }

    override fun withdrawOutput(output: VideoOutput) {
        // A screen being replaced may withdraw after its successor has lent: that is not ours to take.
        if (outputs.value !== output) return

        // Before returning, because the caller destroys the output next.
        receiver.setOutput(null)
        outputs.value = null
    }

    // ---- The session --------------------------------------------------------------------------

    private fun beginRun() {
        val started = Run()
        run = started
        val job = scope.launch(start = CoroutineStart.LAZY) { drive(started) }
        started.job = job
        job.start()
    }

    private suspend fun drive(run: Run) {
        try {
            driveCore(run)
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (unexpected: Throwable) {
            // Nothing here is expected to throw; if it does, the session ends with the reason.
            if (!run.ending) requestTeardown(run, Ending.fail(SessionFault.unexpected(unexpected), run.hasStreamed))
        }
    }

    private suspend fun driveCore(run: Run) {
        var lostAt: Long? = null
        while (true) {
            val reached = lostAt?.let { attemptWithin(run, it + options.hostReturnMillis) } ?: attemptUntilStreaming(run)
            if (!reached) {
                val cause = run.lastFault ?: lostFault()
                end(run, Ending.fail(cause, hasStreamed = true, detail = "The PC did not reconnect within ${options.hostReturnMillis / 1_000} s.", tellHost = false))
            }

            when (val ended = watchStream(run)) {
                is StreamEnd.Link -> when (ended.end.kind) {
                    LinkEndKind.LOST -> if (ended.end.fault?.isRetryable != false) {
                        val cause = ended.end.fault ?: lostFault()
                        streaming = false
                        receiver.stop()
                        dispatch(run, ConnectionEvent.ConnectionLost(cause))
                        run.lastFault = cause
                        lostAt = clock.elapsedMillis()
                    } else {
                        end(run, Ending.fail(ended.end.fault.asTerminal(), run.hasStreamed))
                    }
                    LinkEndKind.PEER_ENDED -> {
                        // Not an error: the PC ended the session.
                        dispatch(run, ConnectionEvent.DisconnectRequested)
                        end(run, Ending.stop("the PC ended the session"))
                    }
                    LinkEndKind.FATAL, LinkEndKind.CLOSED -> end(run, Ending.fail((ended.end.fault ?: unrequested()).asTerminal(), run.hasStreamed))
                }
                is StreamEnd.Receiver -> end(run, Ending.fail(ended.fault.asTerminal(), run.hasStreamed))
                StreamEnd.NoOutput -> {
                    // Nobody is looking: say goodbye so that the PC stops cleanly instead of retrying.
                    dispatch(run, ConnectionEvent.DisconnectRequested)
                    end(run, Ending.stop("nothing has been shown on the device for ${options.outputGraceMillis / 1_000} s"))
                }
            }
        }
    }

    /** Attempts until one reaches Streaming. Returns true; never returns false (the session waits as long as the user does). */
    private suspend fun attemptUntilStreaming(run: Run): Boolean {
        while (true) {
            val failure = attempt(run) ?: return true
            if (!failure.isRetryable) end(run, Ending.fail(failure, run.hasStreamed))
            dispatch(run, ConnectionEvent.AttemptFailed(failure))
            run.lastFault = failure
            clock.delay(options.retryPauseMillis)
        }
    }

    /** As [attemptUntilStreaming], but false if [deadline] passes first: the lost host did not come back. */
    private suspend fun attemptWithin(run: Run, deadline: Long): Boolean = coroutineScope {
        var expired = false
        val attempts = async { attemptUntilStreaming(run) }
        val timer = launch {
            clock.delay(deadline - clock.elapsedMillis())
            expired = true
            attempts.cancel()
        }
        try {
            attempts.await()
        } catch (cancelled: CancellationException) {
            if (expired && currentCoroutineContext().isActive) false else throw cancelled
        } finally {
            timer.cancel()
        }
    }

    /** One attempt, from waiting for a host to the stream accepted. Null when it reached Streaming; otherwise the fault. */
    private suspend fun attempt(run: Run): SessionFault? {
        when (val host = link.awaitHost()) {
            is AttemptOutcome.Succeeded -> Unit
            is AttemptOutcome.Failed -> return host.fault
            AttemptOutcome.Cancelled -> unrequested(run)
        }
        dispatch(run, ConnectionEvent.TransportConnected)

        val stream = when (val offered = link.exchangeCapabilities(receiver.decoders())) {
            is AttemptOutcome.Succeeded -> offered.value
            is AttemptOutcome.Failed -> return offered.fault
            AttemptOutcome.Cancelled -> unrequested(run)
        }

        // A decoder that cannot be had for this stream is not worth waiting for another host over:
        // the PC is told (stream_config_rejected) and the session ends.
        val decoder = when (val chosen = receiver.prepare(stream)) {
            is AttemptOutcome.Succeeded -> chosen.value
            is AttemptOutcome.Failed -> end(run, Ending.fail(chosen.fault.asTerminal(), run.hasStreamed))
            AttemptOutcome.Cancelled -> unrequested(run)
        }

        // Ready before the link says so, because video may follow at once.
        receiver.start(stream, decoder) { reason -> link.requestKeyframe(reason) }
        currentStream.value = StreamInfo(stream.width, stream.height, stream.frameRate, stream.hostName, stream.inputOffered, stream.extended)
        when (val accepted = link.acceptStream(decoder, receiver.sink)) {
            is AttemptOutcome.Succeeded -> Unit
            is AttemptOutcome.Failed -> {
                receiver.stop()
                return accepted.fault
            }
            AttemptOutcome.Cancelled -> unrequested(run)
        }

        dispatch(run, ConnectionEvent.NegotiationCompleted)
        run.hasStreamed = true
        streaming = true

        // The PC sends a keyframe at the start of every stream. If there is an output to show it
        // on, ask as well: it costs one small message and does not rely on the PC alone.
        if (outputs.value != null) link.requestKeyframe(REASON_STARTUP)
        return null
    }

    private sealed interface StreamEnd {
        data class Link(val end: LinkEnd) : StreamEnd
        data class Receiver(val fault: SessionFault) : StreamEnd
        data object NoOutput : StreamEnd
    }

    /** Waits, while streaming, for the first of: the link ending, the receiver failing, no output for the grace period. */
    private suspend fun watchStream(run: Run): StreamEnd = coroutineScope {
        val ended = CompletableDeferred<StreamEnd>()
        val watchers = listOf(
            launch { ended.complete(StreamEnd.Link(link.awaitEnd())) },
            launch { receiver.awaitFault()?.let { ended.complete(StreamEnd.Receiver(it)) } },
            launch {
                outputs.collectLatest { output ->
                    if (output == null) {
                        clock.delay(options.outputGraceMillis)
                        ended.complete(StreamEnd.NoOutput)
                    }
                }
            },
        )
        try {
            ended.await()
        } finally {
            watchers.forEach { it.cancel() }
        }
    }

    /**
     * Dispatches a "what happened" event. The machine rejects one only when a stop was dispatched
     * before the stop's own work reached [scope]; the session then ends as a stop here.
     */
    private fun dispatch(run: Run, event: ConnectionEvent) {
        if (!apply(event)) end(run, Ending.stop(STOPPED_ON_DEVICE))
    }

    /** Ends the run from inside its own work: begins teardown and unwinds the work. */
    private fun end(run: Run, ending: Ending): Nothing {
        requestTeardown(run, ending)
        throw CancellationException("The session is ending.")
    }

    private fun unrequested(run: Run): Nothing = end(run, Ending.fail(unrequested(), run.hasStreamed))

    // ---- Teardown -----------------------------------------------------------------------------

    private fun requestTeardown(run: Run, ending: Ending) {
        if (run.ending) return
        run.ending = true
        streaming = false
        run.job.cancel()
        scope.launch { teardown(run, ending) }
    }

    private suspend fun teardown(run: Run, ending: Ending) {
        val deadline = clock.elapsedMillis() + options.teardownDeadlineMillis
        try {
            // The work in progress was cancelled, which (by the contract) closes whatever it was
            // blocked on. It is waited for so that no call of this run is still running on the link
            // or the receiver while teardown calls them.
            if (!awaitUntil(run.job, deadline)) {
                abandoned(run, "the step in progress when the session began to end", run.job, releaseWhenDone = true)
            }
            step(run, "end the link", deadline) { link.endLink(ending.reason, ending.faultForHost) }
            step(run, "stop the receiver", deadline) { receiver.stop() }
            step(run, "stop listening", deadline) { link.stopListening() }
        } finally {
            currentStream.value = null
            lastTeardownFaults = synchronized(run.cleanupFaults) { run.cleanupFaults.toList() }
            this.run = null

            // Last, so that whoever sees a terminal phase can rely on nothing of the session running
            // except what was abandoned, which isReleased reports.
            val failed = ending.error?.let { apply(ConnectionEvent.Failed(it)) } ?: false
            if (!failed) apply(ConnectionEvent.TeardownCompleted)
        }
    }

    private suspend fun step(run: Run, name: String, deadline: Long, block: suspend () -> Unit) {
        val job = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            try {
                block()
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Throwable) {
                record(run, SessionFault(FaultCategory.CLEANUP, SessionFaultCodes.TEARDOWN_STEP_FAILED, "'$name' failed: ${failure.javaClass.simpleName}: ${failure.message}", FaultTreatment.TERMINAL))
            }
        }
        if (!awaitUntil(job, deadline)) abandoned(run, name, job, releaseWhenDone = false)
    }

    /** True if [job] finished before [deadline] on the session clock. */
    private suspend fun awaitUntil(job: Job, deadline: Long): Boolean {
        if (job.isCompleted) return true
        val remaining = deadline - clock.elapsedMillis()
        if (remaining <= 0) return false
        return coroutineScope {
            val finished = CompletableDeferred<Boolean>()
            val handle = job.invokeOnCompletion { finished.complete(true) }
            val timer = launch {
                clock.delay(remaining)
                finished.complete(false)
            }
            try {
                finished.await()
            } finally {
                handle.dispose()
                timer.cancel()
            }
        }
    }

    /**
     * Records a step left running at the deadline and holds new sessions off until it returns. If
     * it was the session's own work, which may have accepted a connection or started listening on
     * its way out, the link and the receiver are released again once it has returned.
     */
    private fun abandoned(run: Run, name: String, job: Job, releaseWhenDone: Boolean) {
        record(run, SessionFault(FaultCategory.CLEANUP, SessionFaultCodes.TEARDOWN_STEP_OVERRAN, "'$name' had not returned by the teardown deadline and was left running.", FaultTreatment.TERMINAL))
        unreleased.incrementAndGet()
        job.invokeOnCompletion {
            if (!releaseWhenDone) {
                unreleased.decrementAndGet()
            } else {
                scope.launch {
                    try {
                        runCatching { link.endLink("the session had already ended") }
                        runCatching { receiver.stop() }
                        runCatching { link.stopListening() }
                    } finally {
                        unreleased.decrementAndGet()
                    }
                }
            }
        }
    }

    private fun record(run: Run, fault: SessionFault) {
        synchronized(run.cleanupFaults) {
            if (run.cleanupFaults.size < MAX_TEARDOWN_FAULTS) run.cleanupFaults.add(fault)
        }
    }

    // ---- Run state ----------------------------------------------------------------------------

    /** How a run is to end. A stop has no error; a failure has one. */
    private class Ending(val reason: String, val faultForHost: SessionFault?, val error: ConnectionError?) {
        companion object {
            fun stop(reason: String) = Ending(reason, null, null)

            fun fail(fault: SessionFault, hasStreamed: Boolean, detail: String? = null, tellHost: Boolean = true) =
                Ending(fault.message, fault.takeIf { tellHost }, SessionErrors.from(fault, hasStreamed, detail))
        }
    }

    /** Everything one accepted request to wait for a host owns. Touched only on [scope], except [cleanupFaults]. */
    private class Run {
        lateinit var job: Job
        var ending = false
        var hasStreamed = false
        var lastFault: SessionFault? = null
        val cleanupFaults = mutableListOf<SessionFault>()
    }

    private companion object {
        const val MAX_TEARDOWN_FAULTS = 16
        const val STOPPED_ON_DEVICE = "the session was stopped on the device"

        // Protocol section 10 reasons.
        const val REASON_STARTUP = "startup"
        const val REASON_SURFACE_RECREATED = "surface_recreated"

        fun SessionFault.asTerminal(): SessionFault = if (isRetryable) copy(treatment = FaultTreatment.TERMINAL) else this

        fun unrequested() = SessionFault(
            FaultCategory.INTERNAL,
            SessionFaultCodes.UNREQUESTED_CANCELLATION,
            "A step reported that it was cancelled although nothing had cancelled it.",
            FaultTreatment.TERMINAL,
        )

        fun lostFault() = SessionFault(FaultCategory.CONNECTION, SessionFaultCodes.CONNECTION_LOST, "The connection to the PC was lost.", FaultTreatment.RETRYABLE)
    }
}
