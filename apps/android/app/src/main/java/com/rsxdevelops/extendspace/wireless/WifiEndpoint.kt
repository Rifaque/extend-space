package com.rsxdevelops.extendspace.wireless

import android.annotation.SuppressLint
import com.rsxdevelops.extendspace.connection.TransportKind
import com.rsxdevelops.extendspace.link.HostConnection
import com.rsxdevelops.extendspace.link.HostListener
import com.rsxdevelops.extendspace.link.HostListenerFactory
import com.rsxdevelops.extendspace.protocol.Bye
import com.rsxdevelops.extendspace.protocol.ControlCodec
import com.rsxdevelops.extendspace.protocol.ControlMessage
import com.rsxdevelops.extendspace.protocol.ErrorCode
import com.rsxdevelops.extendspace.protocol.ErrorMessage
import com.rsxdevelops.extendspace.protocol.FrameParser
import com.rsxdevelops.extendspace.protocol.Hello
import com.rsxdevelops.extendspace.protocol.PairCommit
import com.rsxdevelops.extendspace.protocol.PairNonce
import com.rsxdevelops.extendspace.protocol.PairResult
import com.rsxdevelops.extendspace.protocol.PairReveal
import com.rsxdevelops.extendspace.protocol.Protocol
import com.rsxdevelops.extendspace.protocol.ProtocolException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.BindException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509ExtendedTrustManager

/** Where pairing is, as the tablet's screen shows it (D77). */
sealed interface PairingState {
    /** No pairing window is open. */
    data object Closed : PairingState

    /** The window is open and nobody has connected yet. */
    data class Waiting(val deviceName: String) : PairingState

    /** A PC has connected: both screens show [code]; the user confirms or cancels. */
    data class Confirm(val code: String, val hostName: String) : PairingState

    /** The attempt is over. The window has closed. */
    data class Finished(val paired: Boolean, val message: String) : PairingState
}

internal data class WifiEndpointOptions(
    /** The port the tablet listens on; if taken, any free one (it is advertised either way). */
    val port: Int = 27_184,
    val bindAddress: InetAddress? = null,
    val handshakeMillis: Int = Protocol.HANDSHAKE_STEP_TIMEOUT_MS,
    /** TLS handshakes running at once; a further connection is closed at once. */
    val maxHandshakes: Int = 4,
    val pairingWindowMillis: Long = 120_000,
    val decisionMillis: Long = 120_000,
)

/** Publishes the endpoint by DNS-SD (D78). Android's `NsdManager` in the app; nothing in tests. */
internal interface ServiceAdvertiser {
    fun advertise(name: String, port: Int, attributes: Map<String, String>)

    fun withdraw()
}

internal object WifiTls {
    const val SESSION_PROTOCOL = "exsp/0"
    const val PAIRING_PROTOCOL = "exsp-pair/0"

    fun serverContext(identity: () -> DeviceIdentity): SSLContext =
        SSLContext.getInstance("TLS").apply { init(arrayOf(IdentityKeyManager(identity)), arrayOf(AnyPeerCertificate), SecureRandom()) }

    /** Mutual TLS 1.3 with both ALPN names; the decision about the peer's key is made after the handshake. */
    fun configureServer(socket: SSLSocket) {
        socket.useClientMode = false
        socket.enabledProtocols = arrayOf("TLSv1.3")
        socket.sslParameters = socket.sslParameters.apply {
            needClientAuth = true
            applicationProtocols = arrayOf(SESSION_PROTOCOL, PAIRING_PROTOCOL)
        }
    }

    /**
     * Accepts any certificate during the handshake: there is no CA, and whether the PC is trusted is
     * decided from its key right after the handshake, before any application data is read (D76).
     */
    @SuppressLint("CustomX509TrustManager", "TrustAllX509TrustManager")
    private object AnyPeerCertificate : X509ExtendedTrustManager() {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = requireOne(chain)
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) = requireOne(chain)
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: javax.net.ssl.SSLEngine?) = requireOne(chain)
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw java.security.cert.CertificateException("This side is never a TLS client.")
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) = checkServerTrusted(chain, authType)
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: javax.net.ssl.SSLEngine?) = checkServerTrusted(chain, authType)
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

        private fun requireOne(chain: Array<out X509Certificate>?) {
            if (chain.isNullOrEmpty()) throw java.security.cert.CertificateException("The PC presented no certificate.")
        }
    }
}

/**
 * The tablet's Wi-Fi endpoint (transport architecture section 3): one TCP listener, running only while
 * the session is waiting with Wi-Fi allowed or the pairing window is open. Every connection is mutual
 * TLS 1.3; then, by ALPN, a session connection from a **trusted** PC is handed to the session link, an
 * untrusted one is told `not_paired` and closed without anything being read, and a pairing connection
 * runs the ceremony if the window is open. The endpoint is advertised by DNS-SD while it runs.
 */
internal class WifiEndpoint(
    private val identity: () -> DeviceIdentity,
    private val trust: TrustedHostStore,
    private val deviceName: () -> String,
    private val appVersion: () -> String,
    private val advertiser: ServiceAdvertiser? = null,
    private val options: WifiEndpointOptions = WifiEndpointOptions(),
    private val log: (String) -> Unit = {},
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val lock = Any()
    private val random = SecureRandom()
    private val pairingState = MutableStateFlow<PairingState>(PairingState.Closed)
    private val handshakes = Semaphore(options.maxHandshakes)
    private val context by lazy { WifiTls.serverContext(identity) }

    // Under [lock].
    private var server: ServerSocket? = null
    private var sessions: LinkedBlockingQueue<Any>? = null
    private var pairingOpen = false
    private var pairingBusy = false
    private var pairingDeadline = 0L
    private var pairingWindow = 0
    private var decisions: LinkedBlockingQueue<PairingEvent>? = null

    private val refused = java.util.concurrent.atomic.AtomicInteger()

    /** Connections turned away: over the handshake limit, failed TLS, unpaired, or an unknown protocol. */
    val refusedPeers: Int get() = refused.get()

    val pairing: StateFlow<PairingState> = pairingState.asStateFlow()

    /** The port listened on, while listening. */
    val port: Int? get() = synchronized(lock) { server?.localPort }

    /** The session side: what the session link listens on for Wi-Fi (through `CompositeHostListener`). */
    val sessionListenerFactory = HostListenerFactory {
        val queue = LinkedBlockingQueue<Any>()
        synchronized(lock) {
            check(sessions == null) { "Sessions are already being listened for." }
            sessions = queue
            ensureRunning()
        }
        SessionListener(queue)
    }

    // ---- Pairing window (the tablet user's side) ---------------------------------------------

    /** Opens the pairing window, starting the endpoint if needed. */
    fun openPairing() {
        val window = synchronized(lock) {
            if (pairingOpen) return
            pairingOpen = true
            pairingDeadline = clock() + options.pairingWindowMillis
            ensureRunning()
            ++pairingWindow
        }
        pairingState.value = PairingState.Waiting(deviceName())
        readvertise()
        log("pairing window open")

        // The window closes by itself if no PC has started pairing by its deadline.
        Thread(
            {
                try {
                    Thread.sleep(options.pairingWindowMillis)
                } catch (interrupted: InterruptedException) {
                    return@Thread
                }
                val expired = synchronized(lock) { pairingWindow == window && pairingOpen && !pairingBusy }
                if (expired) {
                    closePairing()
                    pairingState.value = PairingState.Finished(false, "No PC started pairing in time. Tap Pair with a PC to try again.")
                }
            },
            "extendspace-pairing-window",
        ).apply { isDaemon = true }.start()
    }

    /** Closes the window; cancels an attempt in progress. */
    fun closePairing() {
        val pending = synchronized(lock) {
            pairingOpen = false
            decisions.also { stopIfUnused() }
        }
        pending?.offer(PairingEvent.Local(false))
        if (pairingState.value !is PairingState.Finished) pairingState.value = PairingState.Closed
        readvertise()
    }

    /** The user compared the codes and chose [accepted]. */
    fun decide(accepted: Boolean) {
        synchronized(lock) { decisions }?.offer(PairingEvent.Local(accepted))
    }

    /** Clears a finished attempt's message. */
    fun acknowledgePairing() {
        if (pairingState.value is PairingState.Finished) pairingState.value = PairingState.Closed
    }

    // ---- Lifecycle ---------------------------------------------------------------------------

    private fun ensureRunning() {
        if (server != null) return
        val socket = ServerSocket().apply { reuseAddress = true }
        try {
            socket.bind(InetSocketAddress(options.bindAddress, options.port))
        } catch (taken: BindException) {
            socket.bind(InetSocketAddress(options.bindAddress, 0))
        }
        server = socket
        Thread({ acceptLoop(socket) }, "extendspace-wifi-accept").apply { isDaemon = true }.start()
        log("listening for PCs on Wi-Fi, port ${socket.localPort}")
        readvertise()
    }

    private fun stopIfUnused() {
        if (sessions != null || pairingOpen) return
        val stopping = server ?: return
        server = null
        try {
            stopping.close()
        } catch (exception: IOException) {
            // Already closed.
        }
        advertiser?.withdraw()
        log("stopped listening on Wi-Fi")
    }

    private fun readvertise() {
        val listening = synchronized(lock) { server?.localPort?.let { it to pairingOpen } } ?: return
        val id = try {
            identity().deviceId
        } catch (exception: Exception) {
            log("wireless identity unavailable: ${exception.javaClass.simpleName}")
            return
        }
        advertiser?.advertise(deviceName(), listening.first, mapOf("id" to id.take(16), "v" to Protocol.MAJOR_VERSION.toString(), "p" to if (listening.second) "1" else "0"))
    }

    private inner class SessionListener(private val queue: LinkedBlockingQueue<Any>) : HostListener {
        @Volatile private var closed = false

        override fun accept(): HostConnection {
            while (true) {
                val next = queue.take()
                if (closed || next === STOP) throw IOException("The Wi-Fi listener was closed.")
                return next as HostConnection
            }
        }

        override fun close() {
            if (closed) return
            closed = true
            val stale = synchronized(lock) {
                if (sessions === queue) sessions = null
                stopIfUnused()
                generateSequence { queue.poll() }.toList()
            }
            stale.filterIsInstance<HostConnection>().forEach { it.close() }
            queue.offer(STOP)
        }
    }

    // ---- Connections -------------------------------------------------------------------------

    private fun acceptLoop(listening: ServerSocket) {
        while (true) {
            val plain = try {
                listening.accept()
            } catch (exception: IOException) {
                return
            }
            if (!handshakes.tryAcquire()) {
                // More connections at once than any PC makes: refused before any TLS work. Counted
                // first, so that whoever sees the close can also see the count.
                refused.incrementAndGet()
                closeQuietly(plain)
                continue
            }
            Thread(
                {
                    try {
                        handle(plain)
                    } finally {
                        handshakes.release()
                    }
                },
                "extendspace-wifi-connection",
            ).apply { isDaemon = true }.start()
        }
    }

    private fun handle(plain: Socket) {
        val connection = try {
            plain.soTimeout = options.handshakeMillis
            plain.tcpNoDelay = true
            val tls = context.socketFactory.createSocket(plain, plain.inetAddress.hostAddress, plain.port, true) as SSLSocket
            WifiTls.configureServer(tls)
            tls.startHandshake()
            val peer = tls.session.peerCertificates.firstOrNull() as? X509Certificate ?: throw SSLException("No peer certificate.")
            TlsHostConnection(plain, tls, DeviceIds.of(peer), tls.applicationProtocol.orEmpty())
        } catch (exception: IOException) {
            refused.incrementAndGet()
            log("a Wi-Fi connection failed its TLS handshake: ${exception.javaClass.simpleName}")
            closeQuietly(plain)
            return
        }

        when (connection.applicationProtocol) {
            WifiTls.SESSION_PROTOCOL -> {
                if (!trust.isTrusted(connection.peerId)) {
                    refused.incrementAndGet()
                    log("refused PC ${DeviceIds.short(connection.peerId)}: not paired")
                    refuse(connection, ErrorCode.NOT_PAIRED, "This tablet is not paired with this PC. Pair them first.")
                    return
                }
                val queue = synchronized(lock) { sessions }
                if (queue == null) {
                    refuse(connection, ErrorCode.BUSY, "The tablet is not waiting for a PC. Tap Wait for PC on the tablet.")
                    return
                }
                connection.setReadTimeout(0)
                queue.offer(connection)
            }
            WifiTls.PAIRING_PROTOCOL -> pair(connection)
            else -> {
                refused.incrementAndGet()
                connection.close()
            }
        }
    }

    private fun pair(connection: TlsHostConnection) {
        val events = synchronized(lock) {
            if (!pairingOpen || pairingBusy || clock() > pairingDeadline) {
                null
            } else {
                pairingBusy = true
                LinkedBlockingQueue<PairingEvent>().also { decisions = it }
            }
        }
        if (events == null) {
            refuse(connection, ErrorCode.PAIRING_NOT_OPEN, "The tablet is not in pairing mode. On the tablet, tap Pair with a PC.")
            return
        }

        val outcome = try {
            PairingResponder(connection, events).run()
        } catch (exception: IOException) {
            PairingState.Finished(false, "The connection to the PC failed during pairing.")
        } finally {
            connection.close()
        }

        synchronized(lock) {
            pairingBusy = false
            decisions = null
            // One attempt per window: whatever happened, the window closes.
            pairingOpen = false
            stopIfUnused()
        }
        pairingState.value = outcome
        readvertise()
        log("pairing ended: ${if (outcome.paired) "paired" else "not paired"}")
    }

    /** The tablet's half of the ceremony (transport architecture section 5), on the connection's thread. */
    private inner class PairingResponder(private val connection: TlsHostConnection, private val events: LinkedBlockingQueue<PairingEvent>) {
        private val frames = BlockingFrames(connection.input, connection.output)

        fun run(): PairingState.Finished {
            val hello = frames.read()
            if (hello !is Hello || hello.role != "host" || hello.purpose != "pair") return fail("The PC did not ask to pair.")
            frames.write(Protocol.PREFACE + ControlCodec.encode(Hello(Protocol.MAJOR_VERSION, Protocol.MAJOR_VERSION, "client", deviceName(), appVersion(), "pair")))

            val commit = frames.read() as? PairCommit ?: return fail("The PC broke the pairing exchange.")
            val tabletNonce = ByteArray(PairingCode.NONCE_LENGTH).also(random::nextBytes)
            frames.write(ControlCodec.encode(PairNonce(tabletNonce)))
            val reveal = frames.read() as? PairReveal ?: return fail("The PC broke the pairing exchange.")
            if (!MessageDigest.isEqual(PairingCode.commit(reveal.nonce), commit.commitment)) {
                frames.tryWrite(ControlCodec.encode(ErrorMessage(ErrorCode.PAIRING_FAILED, "The reveal does not match the commitment.", true)))
                return PairingState.Finished(false, "The PC's answer did not match what it committed to. Pairing was stopped for safety.")
            }

            val hostName = hello.deviceName.ifBlank { "PC" }
            val code = PairingCode.compute(reveal.nonce, tabletNonce, DeviceIds.toBytes(connection.peerId), DeviceIds.toBytes(identity().deviceId))
            pairingState.value = PairingState.Confirm(code, hostName)

            // The PC's answer is read on a thread of its own; the user's arrives through decide().
            connection.setReadTimeout(options.decisionMillis.toInt())
            Thread(
                {
                    val message = try {
                        generateSequence { frames.read() }.first { it is PairResult || it is ErrorMessage || it is Bye }
                    } catch (exception: IOException) {
                        null
                    }
                    events.offer(PairingEvent.Host(message))
                },
                "extendspace-pairing-reader",
            ).apply { isDaemon = true }.start()

            var local: Boolean? = null
            var hostAnswered = false
            val deadline = clock() + options.decisionMillis
            while (local == null || (local && !hostAnswered)) {
                val remaining = deadline - clock()
                val event = (if (remaining > 0) events.poll(remaining, TimeUnit.MILLISECONDS) else null)
                    ?: return PairingState.Finished(false, "Nobody confirmed the code in time.")
                when (event) {
                    is PairingEvent.Local -> {
                        if (local != null) continue
                        local = event.accepted
                        frames.tryWrite(ControlCodec.encode(PairResult(event.accepted)))
                        if (!event.accepted) return PairingState.Finished(false, "Pairing was cancelled on this tablet.")
                    }
                    is PairingEvent.Host -> {
                        hostAnswered = true
                        val host = event.message
                        if (host !is PairResult || !host.accepted) {
                            return PairingState.Finished(false, if (host is PairResult) "$hostName declined the pairing." else "The PC ended the pairing.")
                        }
                    }
                }
            }

            trust.add(TrustedHost(connection.peerId, hostName, clock()))
            frames.tryWrite(ControlCodec.encode(Bye("paired")))
            return PairingState.Finished(true, "Paired with $hostName.")
        }

        private fun fail(message: String): PairingState.Finished {
            frames.tryWrite(Protocol.PREFACE + ControlCodec.encode(ErrorMessage(ErrorCode.PAIRING_FAILED, message, true)))
            return PairingState.Finished(false, message)
        }
    }

    /** Tells the PC why, then closes. The PC's own first bytes are drained so that the close does not reset the message away. */
    private fun refuse(connection: TlsHostConnection, code: String, message: String) {
        try {
            connection.output.write(Protocol.PREFACE + ControlCodec.encode(ErrorMessage(code, message, true)))
            connection.output.flush()
            connection.setReadTimeout(500)
            val sink = ByteArray(4096)
            while (connection.input.read(sink) >= 0) {
                // Drained until the PC closes, or for at most the read timeout.
            }
        } catch (exception: IOException) {
            // The PC has gone; nothing more to tell it.
        } finally {
            connection.close()
        }
    }

    private sealed interface PairingEvent {
        data class Local(val accepted: Boolean) : PairingEvent

        data class Host(val message: ControlMessage?) : PairingEvent
    }

    private companion object {
        val STOP = Any()

        fun closeQuietly(socket: Socket) {
            try {
                socket.close()
            } catch (exception: IOException) {
                // Nothing to do.
            }
        }
    }
}

/** A connection whose peer has passed TLS with a certificate; the link need not check who it is again. */
internal class TlsHostConnection(
    private val plain: Socket,
    private val tls: SSLSocket,
    /** The PC's device ID, from its certificate. */
    val peerId: String,
    val applicationProtocol: String,
) : HostConnection {
    override val input: InputStream = tls.inputStream
    override val output: OutputStream = tls.outputStream
    override val peerUid: Int? = null
    override val preauthorized: Boolean = true
    override val transport: TransportKind = TransportKind.WIRELESS

    override fun setReadTimeout(millis: Int) {
        tls.soTimeout = millis
    }

    override fun close() {
        // Shutting the plain socket's input wakes a reader blocked in TLS on any thread.
        try {
            plain.shutdownInput()
        } catch (exception: IOException) {
            // Already closed.
        }
        try {
            tls.close()
        } catch (exception: IOException) {
            // Nothing more can be done.
        }
        try {
            plain.close()
        } catch (exception: IOException) {
            // Nothing more can be done.
        }
    }
}

/** Frames read and written on blocking streams: for the pairing exchange, outside a session link. */
internal class BlockingFrames(private val input: InputStream, private val output: OutputStream) {
    private val parser = FrameParser()
    private val frames = ArrayDeque<Pair<Int, ByteArray>>()
    private val buffer = ByteArray(16 * 1024)

    fun write(bytes: ByteArray) {
        output.write(bytes)
        output.flush()
    }

    /** Writes if it can; a failure only means the peer has gone. */
    fun tryWrite(bytes: ByteArray) {
        try {
            write(bytes)
        } catch (exception: IOException) {
            // Gone.
        }
    }

    /** The next known control message. @throws EOFException, SocketTimeoutException, ProtocolException. */
    fun read(): ControlMessage {
        while (true) {
            while (frames.isNotEmpty()) {
                val (type, payload) = frames.removeFirst()
                ControlCodec.decode(type, payload)?.let { return it }
            }
            val read = input.read(buffer)
            if (read < 0) throw EOFException("The peer closed the connection.")
            parser.append(buffer, 0, read) { header, payload, length ->
                if (header.channel != Protocol.CHANNEL_CONTROL) throw ProtocolException(ErrorCode.UNEXPECTED_MESSAGE, "A pairing connection carries control messages only.")
                frames.addLast(header.type to payload.copyOf(length))
            }
        }
    }
}
