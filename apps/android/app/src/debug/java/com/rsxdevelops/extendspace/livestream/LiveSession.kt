package com.rsxdevelops.extendspace.livestream

import android.net.LocalServerSocket
import android.net.LocalSocket
import android.net.LocalSocketAddress
import android.util.Log
import android.view.Surface
import com.rsxdevelops.extendspace.decodertest.DecoderCatalog
import com.rsxdevelops.extendspace.decodertest.h264.AnnexB
import com.rsxdevelops.extendspace.decodertest.h264.NalType
import com.rsxdevelops.extendspace.decodertest.h264.SequenceParameterSet
import com.rsxdevelops.extendspace.livestream.protocol.Bye
import com.rsxdevelops.extendspace.livestream.protocol.ClientCapabilities
import com.rsxdevelops.extendspace.livestream.protocol.ClientStats
import com.rsxdevelops.extendspace.livestream.protocol.ControlCodec
import com.rsxdevelops.extendspace.livestream.protocol.ControlMessage
import com.rsxdevelops.extendspace.livestream.protocol.ControlType
import com.rsxdevelops.extendspace.livestream.protocol.DecoderCapability
import com.rsxdevelops.extendspace.livestream.protocol.ErrorCode
import com.rsxdevelops.extendspace.livestream.protocol.ErrorMessage
import com.rsxdevelops.extendspace.livestream.protocol.FrameHeader
import com.rsxdevelops.extendspace.livestream.protocol.FrameParser
import com.rsxdevelops.extendspace.livestream.protocol.Hello
import com.rsxdevelops.extendspace.livestream.protocol.KeyframeRequest
import com.rsxdevelops.extendspace.livestream.protocol.LatencySummary
import com.rsxdevelops.extendspace.livestream.protocol.Ping
import com.rsxdevelops.extendspace.livestream.protocol.Pong
import com.rsxdevelops.extendspace.livestream.protocol.Protocol
import com.rsxdevelops.extendspace.livestream.protocol.ProtocolException
import com.rsxdevelops.extendspace.livestream.protocol.StreamConfig
import com.rsxdevelops.extendspace.livestream.protocol.StreamReady
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

internal enum class LivePhase { Stopped, Listening, Negotiating, Streaming, Failed }

internal data class LiveOptions(
    /** A decoder to use by name; null lets the platform choose. */
    val decoderName: String? = null,
    /** Ask the decoder for low-latency mode, which a live picture wants. */
    val lowLatency: Boolean = true,
)

internal data class DisplayInfo(val width: Int, val height: Int, val refreshHz: Double)

/** Everything the live screen shows. Counters are for the current connection; nothing is estimated. */
internal data class LiveStatus(
    val phase: LivePhase = LivePhase.Stopped,
    val detail: String = "Not listening.",
    val hostName: String? = null,
    val hostVersion: String? = null,
    val config: StreamConfig? = null,
    val pictureSize: String? = null,
    val decoderName: String? = null,
    val decoderKind: String? = null,
    val lowLatency: String? = null,
    val stats: ClientStats = ClientStats(),
    val receivedFps: Double? = null,
    val decodedFps: Double? = null,
    val renderedFps: Double? = null,
    val bitsPerSecond: Double? = null,
    val receiveLatency: LatencySummary? = null,
    val renderLatency: LatencySummary? = null,
    val roundTripMicros: Long? = null,
    val connections: Int = 0,
    val disconnects: Int = 0,
    val rejectedPeers: Int = 0,
    val keyframeRequests: Int = 0,
    val problems: List<String> = emptyList(),
)

/**
 * The client's end of an ExtendSpace session over USB. It listens on a local socket in the
 * abstract namespace, which is not reachable from any network; the host reaches it through
 * `adb forward`. One connection is served at a time: handshake, then video in and keepalive
 * replies, statistics and keyframe requests out, until either side ends it. Then it listens again.
 *
 * [start], [stop] and [setSurface] are called from the UI thread and return quickly. Socket reads,
 * the decoder and the statistics timer each run on their own thread; [onStatus] is called from them.
 */
internal class LiveSession(
    private val deviceName: String,
    private val appVersion: String,
    private val display: DisplayInfo,
    private val options: LiveOptions,
    private val onStatus: (LiveStatus) -> Unit,
) {
    private val stateLock = Any()
    private val writeLock = Any()
    private val decoderLock = Any()

    @Volatile private var stopped = true
    @Volatile private var surface: Surface? = null
    @Volatile private var decoderFailure: String? = null

    /** When bytes last arrived from the host, so that a loss can be logged with how long it took to notice. */
    @Volatile private var lastReadNanos = 0L
    private var acceptThread: Thread? = null
    private var server: LocalServerSocket? = null
    private var connection: LocalSocket? = null
    private var output: OutputStream? = null
    private var statsTimer: ScheduledExecutorService? = null
    private var decoder: LiveDecoder? = null
    private var status = LiveStatus()

    private val clock = HostClockEstimate()
    private val receiveLatency = LatencyTracker()
    private val renderLatency = LatencyTracker()

    private val framesReceived = AtomicLong()
    private val bytesReceived = AtomicLong()
    private val framesDiscarded = AtomicLong()
    private val framesSubmitted = AtomicLong()
    private val framesRendered = AtomicLong()
    private val framesDisplayed = AtomicLong()
    private val decoderErrors = AtomicLong()

    // Owned by the reader thread.
    private var gate = VideoGate()
    private val keyframeLimiter = RequestLimiter()
    private var chainBreakReason = REASON_STARTUP

    // Owned by the statistics timer.
    private var previousStats = ClientStats()
    private var previousStatsNanos = 0L

    /** Starts listening. Does nothing if already started. */
    fun start() {
        synchronized(stateLock) {
            if (!stopped) return
            stopped = false
            acceptThread = Thread(::acceptLoop, "extendspace-live-accept").also { it.start() }
        }
    }

    /**
     * Ends the session: says goodbye if connected, stops listening and releases everything. Safe to
     * call more than once and from the UI thread. It returns at once; the work is done on a thread
     * of its own, because every part of it can wait on the other device. A write to a host that has
     * stopped reading blocks for the socket's five-second timeout, and this used to be called
     * straight from the UI thread when the screen was closed.
     */
    fun stop() {
        val accepting: Thread?
        synchronized(stateLock) {
            if (stopped) return
            stopped = true
            accepting = acceptThread
            acceptThread = null
        }

        Thread({ shutDown(accepting) }, "extendspace-live-stop").start()
    }

    /** Runs on the stop thread, once. */
    private fun shutDown(accepting: Thread?) {
        val wasStreaming = snapshotStatus().phase == LivePhase.Streaming
        sayGoodbyeThenClose(
            budgetMillis = GOODBYE_BUDGET_MS,
            goodbye = {
                // The counters as they stand, so that the host's totals do not stop at the last one-second report.
                if (wasStreaming) trySend(currentStats(null, null))
                trySend(Bye("the live screen was closed on the device"))
            },
            // Closing is also what frees a write that is stuck, the goodbye's or the statistics timer's.
            close = ::closeConnection,
        )
        wakeAccept()
        accepting?.join(TimeUnit.SECONDS.toMillis(3))
        publish { it.copy(phase = LivePhase.Stopped, detail = "Not listening.") }
    }

    /**
     * Gives the session the surface to decode onto, or takes it away. Taking it away releases the
     * decoder before returning, because a surface must not be destroyed under a decoder using it.
     */
    fun setSurface(newSurface: Surface?) {
        surface = newSurface
        if (newSurface == null) releaseDecoder()
    }

    private fun acceptLoop() {
        val listening = try {
            // A session that was stopped a moment ago finishes on its own thread and may still hold
            // the name; give it the time its goodbye is allowed, and a little more.
            retryOnIoFailure(BIND_ATTEMPTS, BIND_PAUSE_MS, keepTrying = { !stopped }) { LocalServerSocket(Protocol.LOCAL_SOCKET_NAME) }
        } catch (exception: IOException) {
            publish {
                it.copy(
                    phase = LivePhase.Failed,
                    detail = "Could not listen on the local socket '${Protocol.LOCAL_SOCKET_NAME}': ${exception.message}. Is another copy of this screen open?",
                )
            }
            synchronized(stateLock) { stopped = true }
            return
        }
        synchronized(stateLock) { server = listening }

        try {
            while (!stopped) {
                publish { it.copy(phase = LivePhase.Listening, detail = it.detail.takeIf { _ -> it.phase != LivePhase.Stopped } ?: LISTENING) }
                val socket = try {
                    listening.accept()
                } catch (exception: IOException) {
                    if (!stopped) publish { it.copy(phase = LivePhase.Failed, detail = "Listening stopped unexpectedly: ${exception.message}") }
                    break
                }

                if (stopped) {
                    socket.close()
                    break
                }

                serve(socket)
            }
        } finally {
            synchronized(stateLock) { server = null }
            try {
                listening.close()
            } catch (exception: IOException) {
                Log.w(LOG_TAG, "Closing the listening socket failed: ${exception.message}")
            }
        }
    }

    /** One connection, from accept to close. Never throws: whatever ends it is reported in the status. */
    private fun serve(socket: LocalSocket) {
        // The socket is in the abstract namespace, where any app on the device could try to
        // connect. Only adb's daemon, which runs as the shell user, is who this is for.
        val peer = try {
            socket.peerCredentials.uid
        } catch (exception: IOException) {
            -1
        }
        if (peer != UID_SHELL && peer != UID_ROOT) {
            socket.close()
            publish { it.copy(rejectedPeers = it.rejectedPeers + 1, problems = (it.problems + "Refused a connection from uid $peer; only adb may connect.").takeLast(MAX_PROBLEMS)) }
            return
        }

        var ending = "The connection to the host was lost."
        try {
            synchronized(stateLock) {
                connection = socket
                output = socket.outputStream
            }
            resetForConnection()
            publish { LiveStatus(phase = LivePhase.Negotiating, detail = "A host connected; handshake in progress.", connections = it.connections + 1, disconnects = it.disconnects, rejectedPeers = it.rejectedPeers, problems = it.problems) }

            socket.soTimeout = Protocol.HANDSHAKE_STEP_TIMEOUT_MS
            lastReadNanos = System.nanoTime()
            val reader = ConnectionReader(socket.inputStream) { lastReadNanos = System.nanoTime() }
            val config = handshake(reader)

            socket.soTimeout = Protocol.KEEPALIVE_TIMEOUT_MS
            startStatsTimer()
            publish { it.copy(phase = LivePhase.Streaming, detail = "Streaming from ${it.hostName ?: "the host"}.") }
            ending = readLoop(reader, config)
        } catch (exception: ProtocolException) {
            ending = "The host broke the protocol (${exception.code}): ${exception.message}"
            trySend(ErrorMessage(exception.code, exception.message.orEmpty(), fatal = true))
        } catch (exception: IOException) {
            ending = if (stopped) {
                "Stopped."
            } else {
                // The handshake and the keepalive timeouts are the same length, so one figure serves both.
                describeLostConnection(
                    quietMillis = (System.nanoTime() - lastReadNanos) / NANOS_PER_MILLI,
                    timeoutMillis = Protocol.KEEPALIVE_TIMEOUT_MS,
                    cause = exception.message ?: exception.javaClass.simpleName,
                )
            }
        } catch (exception: RuntimeException) {
            ending = "An internal error ended the connection: ${exception.javaClass.simpleName}: ${exception.message}"
            trySend(ErrorMessage(ErrorCode.INTERNAL_ERROR, ending, fatal = true))
            Log.e(LOG_TAG, ending, exception)
        } finally {
            val quietMillis = (System.nanoTime() - lastReadNanos) / NANOS_PER_MILLI
            stopStatsTimer()
            closeConnection()
            releaseDecoder()
            Log.i(LOG_TAG, "Connection ended $quietMillis ms after the last bytes from the host arrived: $ending")
            Log.i(LOG_TAG, LiveSummary.format(snapshotStatus().copy(detail = ending, stats = currentStats(null, null))))
            publish { it.copy(phase = LivePhase.Listening, detail = "$ending Listening again.", disconnects = it.disconnects + 1, stats = currentStats(null, null)) }
        }
    }

    /** Preface and hello each way, capabilities out, stream configuration in, ready out. */
    private fun handshake(reader: ConnectionReader): StreamConfig {
        write(Protocol.PREFACE)
        send(Hello(Protocol.MAJOR_VERSION, Protocol.MAJOR_VERSION, "client", deviceName, appVersion))

        val hello = reader.expect<Hello>()
        if (hello.protocolMin > Protocol.MAJOR_VERSION || hello.protocolMax < Protocol.MAJOR_VERSION) {
            throw ProtocolException(
                ErrorCode.PROTOCOL_VERSION_UNSUPPORTED,
                "The host speaks protocol versions ${hello.protocolMin} to ${hello.protocolMax}; this app speaks ${Protocol.MAJOR_VERSION}.",
            )
        }
        publish { it.copy(hostName = hello.deviceName, hostVersion = hello.appVersion) }

        val decoders = DecoderCatalog.h264Decoders()
        send(
            ClientCapabilities(
                displayWidth = display.width,
                displayHeight = display.height,
                refreshHz = display.refreshHz,
                decoders = decoders.map { info ->
                    val capabilities = info.getCapabilitiesForType(DecoderCatalog.MIME_TYPE)
                    val video = checkNotNull(capabilities.videoCapabilities)
                    DecoderCapability(
                        name = info.name,
                        hardware = info.isHardwareAccelerated,
                        lowLatency = capabilities.isFeatureSupported(FEATURE_LOW_LATENCY),
                        maxWidth = video.supportedWidths.upper,
                        maxHeight = video.supportedHeights.upper,
                    )
                },
            ),
        )

        val config = reader.expect<StreamConfig>()
        val decoderName = chooseDecoder(config)
        val kind = decoders.firstOrNull { it.name == decoderName }?.let(DecoderCatalog::kind)
        publish { it.copy(config = config, decoderName = decoderName, decoderKind = kind) }
        send(StreamReady(decoderName, kind == "hardware"))
        return config
    }

    private fun chooseDecoder(config: StreamConfig): String {
        if (config.codec != "h264") {
            throw ProtocolException(ErrorCode.STREAM_CONFIG_REJECTED, "This app decodes h264 only; the host offered '${config.codec}'.")
        }
        if (config.width <= 0 || config.height <= 0) {
            throw ProtocolException(ErrorCode.STREAM_CONFIG_REJECTED, "The host offered a ${config.width} x ${config.height} stream.")
        }

        val name = options.decoderName ?: DecoderCatalog.platformChoice(config.width, config.height)
        if (name == null || DecoderCatalog.h264Decoders().none { it.name == name }) {
            throw ProtocolException(
                ErrorCode.STREAM_CONFIG_REJECTED,
                "No H.264 decoder on this device ${if (options.decoderName == null) "reports support for ${config.width} x ${config.height}" else "is named ${options.decoderName}"}.",
            )
        }
        return name
    }

    /** Reads until the connection ends. Returns why, for the status line. */
    private fun readLoop(reader: ConnectionReader, config: StreamConfig): String {
        var ending: String? = null
        val handler: (FrameHeader, ByteArray, Int) -> Unit = { header, payload, length ->
            when (header.channel) {
                Protocol.CHANNEL_VIDEO -> onVideo(header, payload, length, config)
                Protocol.CHANNEL_CONTROL -> when (val message = ControlCodec.decode(header.type, payload, length)) {
                    is Ping -> {
                        // Answered before anything else, so that the host's round trip is as short as it can be.
                        send(Pong(message.id, message.hostTimeMicros, nowMicros()))
                        message.clockOffsetMicros?.let { clock.update(it, message.roundTripMicros) }
                    }
                    is Bye -> ending = "The host ended the session: ${message.reason}."
                    is ErrorMessage -> {
                        addProblem("The host reported ${message.code}: ${message.message}")
                        if (message.fatal) ending = "The host reported a fatal error (${message.code}): ${message.message}."
                    }
                    is StreamConfig -> throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "Changing the stream configuration mid-session is not implemented in this build.")
                    null -> Unit // A control type from a later protocol version: ignored, as the protocol requires.
                    else -> throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "The host sent control type ${header.type}, which a host does not send while streaming.")
                }
                else -> throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "The host sent a frame on channel ${header.channel}.")
            }
        }

        while (ending == null) {
            if (!reader.readInto(handler)) return describeEndOfInput(stopped)
        }
        return ending ?: "The connection ended."
    }

    private fun onVideo(header: FrameHeader, payload: ByteArray, length: Int, config: StreamConfig) {
        if (length <= Protocol.VIDEO_HEADER_LENGTH) {
            throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "A video frame of $length bytes is too short to hold its header and a picture.")
        }

        val arrivedMicros = nowMicros()
        val sequence = readUnsignedInt(payload, 0)
        val timestampMicros = readLong(payload, 4)
        val dataOffset = Protocol.VIDEO_HEADER_LENGTH
        val dataLength = length - dataOffset

        framesReceived.incrementAndGet()
        bytesReceived.addAndGet((Protocol.HEADER_LENGTH + length).toLong())
        clock.latencyMicros(arrivedMicros, timestampMicros)?.let(receiveLatency::add)

        // A decoder that failed on its own thread is cleared away here, on the thread that feeds it.
        decoderFailure?.let { failure ->
            decoderFailure = null
            decoderErrors.incrementAndGet()
            addProblem(failure)
            releaseDecoder()
            gate.breakChain()
            chainBreakReason = REASON_DECODE_ERROR
        }

        val target = surface
        val missingBefore = gate.framesMissing
        val decision = gate.onFrame(sequence, header.flags, target != null)
        if (gate.framesMissing != missingBefore) chainBreakReason = REASON_SEQUENCE_GAP

        when (decision) {
            GateDecision.DISCARD_NO_SURFACE -> {
                framesDiscarded.incrementAndGet()
                chainBreakReason = REASON_SURFACE_RECREATED
            }

            GateDecision.DISCARD_AWAITING_KEYFRAME -> {
                framesDiscarded.incrementAndGet()
                requestKeyframe(arrivedMicros)
            }

            GateDecision.DECODE -> {
                val active = if (header.isRandomAccessPoint) {
                    prepareDecoder(checkNotNull(target), payload, dataOffset, dataLength, config)
                } else {
                    synchronized(decoderLock) { decoder }
                }

                val keyFrame = header.flags and Protocol.FLAG_KEYFRAME != 0
                if (active != null && active.submit(payload, dataOffset, dataLength, timestampMicros, keyFrame)) {
                    framesSubmitted.incrementAndGet()
                } else {
                    // No decoder (the surface went away meanwhile), or one that is too far behind.
                    framesDiscarded.incrementAndGet()
                    gate.breakChain()
                    chainBreakReason = if (active == null) REASON_SURFACE_RECREATED else REASON_DECODER_BACKLOG
                    requestKeyframe(arrivedMicros)
                }
            }
        }
    }

    /**
     * Makes sure there is a decoder configured for the parameter sets this keyframe carries: the
     * first keyframe creates it, and a keyframe with different parameter sets replaces it.
     */
    private fun prepareDecoder(target: Surface, payload: ByteArray, offset: Int, length: Int, config: StreamConfig): LiveDecoder? {
        val units = AnnexB.findNalUnits(payload, offset, offset + length)
        val sps = units.firstOrNull { it.type == NalType.SPS }?.let { payload.copyOfRange(it.payload, it.end) }
        val pps = units.firstOrNull { it.type == NalType.PPS }?.let { payload.copyOfRange(it.payload, it.end) }
        if (sps == null || pps == null) {
            throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "A frame flagged CONFIG does not contain an SPS and a PPS.")
        }

        synchronized(decoderLock) {
            decoder?.let { if (it.configuredWith.contentEquals(sps + pps)) return it }
            if (surface !== target) return null

            decoder?.release()
            decoder = null
            val parameters = SequenceParameterSet.parse(sps)
            val name = chooseDecoder(config.copy(width = parameters.width, height = parameters.height))
            val created = try {
                LiveDecoder(target, parameters, sps, pps, name, options.lowLatency, decoderListener)
            } catch (exception: Exception) {
                throw ProtocolException(
                    ErrorCode.STREAM_CONFIG_REJECTED,
                    "The decoder $name could not be started for ${parameters.width} x ${parameters.height}: ${exception.message}",
                )
            }
            decoder = created
            publish {
                it.copy(
                    pictureSize = "${parameters.width} x ${parameters.height}, ${parameters.profileName} profile, level ${parameters.levelName}",
                    decoderName = name,
                    decoderKind = DecoderCatalog.h264Decoders().firstOrNull { info -> info.name == name }?.let(DecoderCatalog::kind),
                    lowLatency = created.lowLatency,
                )
            }
            return created
        }
    }

    private val decoderListener = object : LiveDecoderListener {
        override fun onFrameRendered(timestampMicros: Long, releasedNanos: Long) {
            framesRendered.incrementAndGet()
            clock.latencyMicros(releasedNanos / NANOS_PER_MICRO, timestampMicros)?.let(renderLatency::add)
        }

        override fun onFrameDisplayed(timestampMicros: Long) {
            framesDisplayed.incrementAndGet()
        }

        override fun onDecoderError(message: String) {
            decoderFailure = message
        }
    }

    private fun requestKeyframe(nowMicros: Long) {
        if (!keyframeLimiter.tryAcquire(nowMicros)) return
        send(KeyframeRequest(chainBreakReason))
        publish { it.copy(keyframeRequests = it.keyframeRequests + 1) }
    }

    private fun releaseDecoder() {
        synchronized(decoderLock) {
            decoder?.release()
            decoder = null
        }
    }

    private fun resetForConnection() {
        gate = VideoGate()
        chainBreakReason = REASON_STARTUP
        decoderFailure = null
        clock.clear()
        receiveLatency.drain()
        renderLatency.drain()
        listOf(framesReceived, bytesReceived, framesDiscarded, framesSubmitted, framesRendered, framesDisplayed, decoderErrors).forEach { it.set(0) }
        previousStats = ClientStats()
        previousStatsNanos = System.nanoTime()
    }

    private fun startStatsTimer() {
        val timer = Executors.newSingleThreadScheduledExecutor { Thread(it, "extendspace-live-stats") }
        synchronized(stateLock) { statsTimer = timer }
        timer.scheduleWithFixedDelay(::reportStats, Protocol.STATS_INTERVAL_MS, Protocol.STATS_INTERVAL_MS, TimeUnit.MILLISECONDS)
    }

    private fun stopStatsTimer() {
        val timer = synchronized(stateLock) { statsTimer.also { statsTimer = null } } ?: return
        timer.shutdownNow()
        timer.awaitTermination(1, TimeUnit.SECONDS)
    }

    /** Once a second: tell the host how things stand here, and refresh the screen's rates. */
    private fun reportStats() {
        try {
            val stats = currentStats(receiveLatency.drain(), renderLatency.drain())
            val now = System.nanoTime()
            val seconds = (now - previousStatsNanos) / 1e9
            val previous = previousStats
            previousStats = stats
            previousStatsNanos = now

            trySend(stats)
            publish {
                it.copy(
                    stats = stats,
                    receivedFps = (stats.framesReceived - previous.framesReceived) / seconds,
                    decodedFps = (stats.framesDecoded - previous.framesDecoded) / seconds,
                    renderedFps = (stats.framesRendered - previous.framesRendered) / seconds,
                    bitsPerSecond = (stats.bytesReceived - previous.bytesReceived) * 8 / seconds,
                    receiveLatency = stats.receiveLatency ?: it.receiveLatency,
                    renderLatency = stats.renderLatency ?: it.renderLatency,
                    roundTripMicros = clock.roundTripMicros,
                    lowLatency = synchronized(decoderLock) { decoder?.lowLatency } ?: it.lowLatency,
                )
            }
        } catch (exception: RuntimeException) {
            // A timer that throws is silently cancelled by the executor; say so instead.
            Log.e(LOG_TAG, "Reporting statistics failed", exception)
        }
    }

    private fun currentStats(receive: LatencySummary?, render: LatencySummary?) = ClientStats(
        framesReceived = framesReceived.get(),
        bytesReceived = bytesReceived.get(),
        framesMissing = gate.framesMissing,
        framesDiscarded = framesDiscarded.get(),
        framesSubmitted = framesSubmitted.get(),
        // Every decoded frame is released to the surface at once, so the two counts are one.
        framesDecoded = framesRendered.get(),
        framesRendered = framesRendered.get(),
        framesDisplayed = framesDisplayed.get(),
        decoderErrors = decoderErrors.get(),
        queueDepth = synchronized(decoderLock) { decoder?.queueDepth } ?: 0,
        receiveLatency = receive,
        renderLatency = render,
    )

    private fun send(message: ControlMessage) = write(ControlCodec.encode(message))

    private fun write(bytes: ByteArray) {
        synchronized(writeLock) {
            val stream = synchronized(stateLock) { output } ?: throw IOException("not connected")
            stream.write(bytes)
            stream.flush()
        }
    }

    /** For messages that are worth sending but not worth failing over. */
    private fun trySend(message: ControlMessage) {
        try {
            send(message)
        } catch (exception: IOException) {
            Log.i(LOG_TAG, "Could not send ${message.javaClass.simpleName}: ${exception.message}")
        }
    }

    private fun closeConnection() {
        val socket = synchronized(stateLock) {
            output = null
            connection.also { connection = null }
        } ?: return
        try {
            // Shutting the input down is what wakes a reader blocked on this socket.
            socket.shutdownInput()
            socket.shutdownOutput()
        } catch (exception: IOException) {
            // Already closed by the other side.
        }
        try {
            socket.close()
        } catch (exception: IOException) {
            Log.w(LOG_TAG, "Closing the connection failed: ${exception.message}")
        }
    }

    /** `accept()` does not notice its socket being closed, so it is woken by connecting to it. */
    private fun wakeAccept() {
        if (synchronized(stateLock) { server } == null) return
        try {
            LocalSocket().use { it.connect(LocalSocketAddress(Protocol.LOCAL_SOCKET_NAME, LocalSocketAddress.Namespace.ABSTRACT)) }
        } catch (exception: IOException) {
            Log.i(LOG_TAG, "Waking the listener failed: ${exception.message}")
        }
    }

    private fun addProblem(problem: String) {
        Log.w(LOG_TAG, problem)
        publish { it.copy(problems = (it.problems + problem).takeLast(MAX_PROBLEMS)) }
    }

    private fun snapshotStatus(): LiveStatus = synchronized(stateLock) { status }

    private fun publish(change: (LiveStatus) -> LiveStatus) {
        val updated = synchronized(stateLock) {
            status = change(status)
            status
        }
        onStatus(updated)
    }

    /** Reads from the connection in whatever pieces arrive and feeds the frame parser. */
    private class ConnectionReader(private val input: InputStream, private val onBytes: () -> Unit) {
        private val parser = FrameParser()
        private val buffer = ByteArray(64 * 1024)

        /** Reads once and hands complete frames to [handler]. Returns false when the other side has closed. */
        fun readInto(handler: (FrameHeader, ByteArray, Int) -> Unit): Boolean {
            val read = input.read(buffer)
            if (read < 0) return false
            onBytes()
            parser.append(buffer, 0, read) { header, payload, length -> handler(header, payload, length) }
            return true
        }

        /** Handshake step: the next message must be a [T]. */
        inline fun <reified T : ControlMessage> expect(): T {
            var received: ControlMessage? = null
            var seen = false
            while (!seen) {
                val open = readInto { header, payload, length ->
                    if (seen) throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "The host sent more than one message where one was expected.")
                    if (header.channel != Protocol.CHANNEL_CONTROL) {
                        throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "Expected ${T::class.simpleName}, received a frame on channel ${header.channel}.")
                    }
                    received = ControlCodec.decode(header.type, payload, length)
                    seen = true
                }
                if (!open) throw IOException("the host closed the connection during the handshake")
            }

            return when (val message = received) {
                is T -> message
                is ErrorMessage -> throw IOException("the host reported ${message.code}: ${message.message}")
                is Bye -> throw IOException("the host ended the session during the handshake: ${message.reason}")
                else -> throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "Expected ${T::class.simpleName}, received something else.")
            }
        }
    }

    companion object {
        const val LOG_TAG = "ExtendSpaceLive"
        private const val LISTENING = "Listening on the local socket; waiting for the Windows host to connect through adb."
        private const val MAX_PROBLEMS = 6

        /** How long a goodbye may take before the connection is closed regardless. */
        private const val GOODBYE_BUDGET_MS = 500L
        private const val BIND_ATTEMPTS = 20
        private const val BIND_PAUSE_MS = 100L
        private const val NANOS_PER_MICRO = 1_000L
        private const val NANOS_PER_MILLI = 1_000_000L

        // Android's fixed user IDs for the adb shell user and for root (system/core android_filesystem_config.h).
        private const val UID_ROOT = 0
        private const val UID_SHELL = 2000

        // MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency, a named constant only from API 30.
        private const val FEATURE_LOW_LATENCY = "low-latency"

        private const val REASON_STARTUP = "startup"
        private const val REASON_SEQUENCE_GAP = "sequence_gap"
        private const val REASON_DECODE_ERROR = "decode_error"
        private const val REASON_SURFACE_RECREATED = "surface_recreated"
        private const val REASON_DECODER_BACKLOG = "decoder_backlog"

        fun nowMicros(): Long = System.nanoTime() / NANOS_PER_MICRO

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
