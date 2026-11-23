package com.rsxdevelops.extendspace.link

import com.rsxdevelops.extendspace.connection.AttemptOutcome
import com.rsxdevelops.extendspace.connection.DecoderCapability
import com.rsxdevelops.extendspace.connection.DecoderChoice
import com.rsxdevelops.extendspace.connection.FaultCategory
import com.rsxdevelops.extendspace.connection.FaultTreatment
import com.rsxdevelops.extendspace.connection.LinkEnd
import com.rsxdevelops.extendspace.connection.LinkEndKind
import com.rsxdevelops.extendspace.connection.SessionFault
import com.rsxdevelops.extendspace.connection.StreamDescription
import com.rsxdevelops.extendspace.connection.VideoSink
import com.rsxdevelops.extendspace.protocol.Bye
import com.rsxdevelops.extendspace.protocol.ClientCapabilities
import com.rsxdevelops.extendspace.protocol.ClientStats
import com.rsxdevelops.extendspace.protocol.ControlCodec
import com.rsxdevelops.extendspace.protocol.ErrorCode
import com.rsxdevelops.extendspace.protocol.ErrorMessage
import com.rsxdevelops.extendspace.protocol.Hello
import com.rsxdevelops.extendspace.protocol.KeyframeRequest
import com.rsxdevelops.extendspace.protocol.Ping
import com.rsxdevelops.extendspace.protocol.Pong
import com.rsxdevelops.extendspace.protocol.Protocol
import com.rsxdevelops.extendspace.protocol.ProtocolException
import com.rsxdevelops.extendspace.protocol.StreamReady
import com.rsxdevelops.extendspace.video.HostClockEstimate
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.concurrent.thread

/**
 * The production link against a scripted host over in-memory sockets: listening and the peer check,
 * each handshake step and how each can fail, the reader (video to the sink, pings, the end of the
 * link), the single writer (pongs, statistics, keyframe requests, last words), cancellation of every
 * blocking wait, and closing. Real threads and real time, with short bounds; no device.
 */
class UsbHostLinkTest {
    private val factory = MemoryListenerFactory()
    private val clock = HostClockEstimate()
    private val received = CopyOnWriteArrayList<Pair<Int, ByteArray>>()
    private val sink = VideoSink { flags, payload, length -> received.add(flags to payload.copyOf(length)) }
    private var stats: ClientStats? = null
    private val options = UsbHostLinkOptions(handshakeStepMillis = 1_000, keepaliveMillis = 1_000, statsIntervalMillis = 100, allowedPeerUids = setOf(0, 2000))
    private val link = UsbHostLink(factory, "Fake Pad", { "9.9.9" }, { DisplayInfo(3200, 2136, 144.0) }, { stats }, clock, options)
    private val decoders = listOf(DecoderCapability("c2.fake.avc.decoder", true, true, 4096, 4096))
    private val choice = DecoderChoice("c2.fake.avc.decoder", true)

    @After
    fun tearDown() = runBlocking {
        link.endLink("test over")
        link.stopListening()
    }

    /** A host connects; the link accepts it. */
    private suspend fun connect(connection: MemoryConnection = MemoryConnection(), listeners: Int = 1): TestHost = coroutineScope {
        val accepting = async(Dispatchers.Default) { link.awaitHost() }
        awaitListener(listeners).connect(connection)
        assertEquals(AttemptOutcome.Succeeded(Unit), withTimeout(PATIENCE_MS) { accepting.await() })
        TestHost(connection)
    }

    /** The newest listener, once the link has opened [count] of them. */
    private fun awaitListener(count: Int = 1): MemoryListener {
        val deadline = System.currentTimeMillis() + PATIENCE_MS
        while (synchronized(factory.listeners) { factory.listeners.size < count }) {
            check(System.currentTimeMillis() < deadline) { "the link never listened" }
            Thread.sleep(1)
        }
        return factory.listener
    }

    /** Handshake to Streaming; returns the host and what the host learnt. */
    private suspend fun streaming(): Triple<TestHost, Hello, StreamDescription> {
        val host = connect()
        var hello: Hello? = null
        val hostSide = thread { hello = host.handshake() }
        val stream = (link.exchangeCapabilities(decoders) as AttemptOutcome.Succeeded).value
        hostSide.join(PATIENCE_MS)
        assertEquals(AttemptOutcome.Succeeded(Unit), link.acceptStream(choice, sink))
        host.expect<StreamReady>()
        return Triple(host, hello!!, stream)
    }

    @Test
    fun `a host that connects is accepted and the handshake offers this device and returns the stream`() = runBlocking {
        val host = connect()
        var hello: Hello? = null
        var capabilities: ClientCapabilities? = null
        val hostSide = thread {
            host.sendPreface()
            host.send(Hello(0, 0, "host", "TEST-PC", "1.2.3"))
            hello = host.expect<Hello>()
            capabilities = host.expect<ClientCapabilities>()
            host.send(TestHost.CONFIG_1080P60)
        }

        val stream = link.exchangeCapabilities(decoders)
        hostSide.join(PATIENCE_MS)
        assertEquals(AttemptOutcome.Succeeded(StreamDescription("h264", 1920, 1080, 60, "TEST-PC", "1.2.3")), stream)
        assertEquals(Hello(0, 0, "client", "Fake Pad", "9.9.9"), hello)
        assertEquals(3200 to 2136, capabilities!!.displayWidth to capabilities!!.displayHeight)
        assertEquals("c2.fake.avc.decoder", capabilities!!.decoders.single().name)

        assertEquals(AttemptOutcome.Succeeded(Unit), link.acceptStream(choice, sink))
        assertEquals(StreamReady("c2.fake.avc.decoder", true), host.expect<StreamReady>())
    }

    @Test
    fun `video goes to the sink in order, pings are answered, and the host clock is learnt`() = runBlocking {
        val (host) = streaming()
        host.sendVideo(byteArrayOf(0, 0, 0, 1, 0x65), Protocol.FLAG_KEYFRAME or Protocol.FLAG_CONFIG)
        host.send(Ping(7, 123_456, clockOffsetMicros = 1_000, roundTripMicros = 3_000))
        host.sendVideo(byteArrayOf(0, 0, 0, 1, 0x41), 0)

        val pong = host.expect<Pong>(skip = { it is ClientStats })
        assertEquals(7L to 123_456L, pong.id to pong.hostTimeMicros)
        awaitUntil { received.size == 2 }
        assertEquals(listOf(3, 0), received.map { it.first })
        assertEquals(1, received[1].second[3].toInt())
        assertEquals(3_000L, clock.roundTripMicros)
    }

    @Test
    fun `statistics go out about once per interval with what the receiver reports`() = runBlocking {
        stats = ClientStats(framesReceived = 42, framesRendered = 40)
        val (host) = streaming()
        val first = host.expect<ClientStats>(skip = { it is Pong })
        val second = host.expect<ClientStats>(skip = { it is Pong })
        assertEquals(42L to 40L, first.framesReceived to first.framesRendered)
        assertEquals(first, second)
    }

    @Test
    fun `keyframe requests are never dropped by the link, however close together`() = runBlocking {
        // D61: until D61 the second was dropped, within 500 ms of the first. The host rate-limits
        // without losing any (protocol section 10).
        val (host) = streaming()
        link.requestKeyframe("surface_recreated")
        assertEquals(KeyframeRequest("surface_recreated"), host.expect<KeyframeRequest>())
        link.requestKeyframe("sequence_gap")
        assertEquals(KeyframeRequest("sequence_gap"), host.expect<KeyframeRequest>())
    }

    @Test
    fun `a goodbye from the host ends the link as peer ended and closes it`() = runBlocking {
        val (host) = streaming()
        host.send(Bye("the session was stopped on the PC"))
        assertEquals(LinkEnd(LinkEndKind.PEER_ENDED, peerReason = "the session was stopped on the PC"), withTimeout(PATIENCE_MS) { link.awaitEnd() })
        assertTrue(host.closedWithin(PATIENCE_MS))
    }

    @Test
    fun `a host that closes without a goodbye is a retryable loss`() = runBlocking {
        val (host) = streaming()
        host.connection.toApp.finishWriting()
        val end = withTimeout(PATIENCE_MS) { link.awaitEnd() }
        assertEquals(LinkEndKind.LOST, end.kind)
        assertEquals(FaultCategory.CONNECTION to "host_closed", end.fault!!.category to end.fault!!.code)
        assertTrue(end.fault!!.isRetryable)
    }

    @Test
    fun `a host that goes silent is lost after the keepalive time`() = runBlocking {
        streaming()
        val started = System.nanoTime()
        val end = withTimeout(PATIENCE_MS) { link.awaitEnd() }
        assertEquals(LinkEndKind.LOST, end.kind)
        assertEquals(FaultCategory.TIMEOUT to "keepalive_timeout", end.fault!!.category to end.fault!!.code)
        assertTrue((System.nanoTime() - started) / 1_000_000 >= options.keepaliveMillis - 50)
    }

    @Test
    fun `malformed video ends the link as fatal and the host is told why`() = runBlocking {
        val throwing = UsbHostLink(factory, "Fake Pad", { "9.9.9" }, { DisplayInfo(1, 1, 60.0) }, { null }, clock, options)
        try {
            val accepting = async(Dispatchers.Default) { throwing.awaitHost() }
            awaitListener().connect(MemoryConnection().also { connection ->
                val host = TestHost(connection)
                thread {
                    host.handshake()
                    host.expect<StreamReady>()
                    host.sendVideo(byteArrayOf(1), 0)
                }
            })
            accepting.await()
            throwing.exchangeCapabilities(decoders)
            throwing.acceptStream(choice) { _, _, _ -> throw ProtocolException(ErrorCode.MALFORMED_MESSAGE, "too short") }
            val end = withTimeout(PATIENCE_MS) { throwing.awaitEnd() }
            assertEquals(LinkEndKind.FATAL to ErrorCode.MALFORMED_MESSAGE, end.kind to end.fault!!.code)
            assertEquals(FaultTreatment.TERMINAL, end.fault!!.treatment)
        } finally {
            throwing.endLink("test over")
            throwing.stopListening()
        }
    }

    @Test
    fun `a fatal error from the host ends the link as fatal with its code`() = runBlocking {
        val (host) = streaming()
        host.send(ErrorMessage("internal_error", "the encoder failed", fatal = true))
        val end = withTimeout(PATIENCE_MS) { link.awaitEnd() }
        assertEquals(LinkEndKind.FATAL, end.kind)
        assertEquals(FaultCategory.PROTOCOL to "internal_error", end.fault!!.category to end.fault!!.code)
    }

    @Test
    fun `ending the link says goodbye with the reason and closes`() = runBlocking {
        val (host) = streaming()
        link.endLink("the session was stopped on the device")
        assertEquals(Bye("the session was stopped on the device"), host.expect<Bye>())
        assertTrue(host.closedWithin(PATIENCE_MS))
    }

    @Test
    fun `ending the link for a decoder that cannot be had tells the host stream_config_rejected`() = runBlocking {
        val (host) = streaming()
        link.endLink("no decoder", SessionFault(FaultCategory.DECODER, "decoder_start_failed", "The decoder could not be started.", FaultTreatment.TERMINAL))
        assertEquals(ErrorMessage("stream_config_rejected", "The decoder could not be started.", true), host.expect<ErrorMessage>())
    }

    @Test
    fun `ending the link while the host reads nothing is still bounded`() = runBlocking {
        val connection = MemoryConnection(toHostCapacity = 64)
        val host = connect(connection)
        val hostSide = thread { host.handshake() }
        link.exchangeCapabilities(decoders)
        hostSide.join(PATIENCE_MS)
        link.acceptStream(choice, sink)

        // The host stops reading; the writer fills the pipe with statistics and pongs and blocks.
        stats = ClientStats(framesReceived = 1)
        repeat(20) { host.send(Ping(it.toLong(), 1, null, null)) }
        Thread.sleep(300)

        val started = System.nanoTime()
        link.endLink("stopped")
        val tookMillis = (System.nanoTime() - started) / 1_000_000
        assertTrue("took $tookMillis ms", tookMillis < options.lastWordsMillis + 2 * options.joinMillis + 500)
        assertTrue(connection.closes > 0)
    }

    @Test
    fun `a host speaking another protocol version is refused terminally and told so`() = runBlocking {
        val host = connect()
        val hostSide = thread {
            host.sendPreface()
            host.send(Hello(1, 2, "host", "FUTURE-PC", "9.0"))
        }
        val outcome = link.exchangeCapabilities(decoders) as AttemptOutcome.Failed
        hostSide.join(PATIENCE_MS)
        assertEquals(FaultCategory.PROTOCOL to ErrorCode.PROTOCOL_VERSION_UNSUPPORTED, outcome.fault.category to outcome.fault.code)
        assertEquals(FaultTreatment.TERMINAL, outcome.fault.treatment)
        assertEquals(ErrorCode.PROTOCOL_VERSION_UNSUPPORTED, host.expect<ErrorMessage>(skip = { it is Hello }).code)
    }

    @Test
    fun `a read timeout that fires a little early is still the keepalive timeout`() = runBlocking {
        // Stage 4.1: kernel timers count whole ticks, so a read timeout can end just before its time.
        val early = MemoryConnection().apply { toApp.timeoutEarlyMillis = 20 }
        val host = connect(early)
        val hostSide = thread { host.handshake() }
        link.exchangeCapabilities(decoders)
        hostSide.join(PATIENCE_MS)
        link.acceptStream(choice, sink)

        val end = withTimeout(PATIENCE_MS) { link.awaitEnd() }
        assertEquals(LinkEndKind.LOST, end.kind)
        assertEquals(FaultCategory.TIMEOUT to "keepalive_timeout", end.fault!!.category to end.fault!!.code)
    }

    @Test
    fun `a handshake read timeout that fires a little early is still a handshake timeout`() = runBlocking {
        val host = connect(MemoryConnection().apply { toApp.timeoutEarlyMillis = 20 })
        val outcome = link.exchangeCapabilities(decoders) as AttemptOutcome.Failed
        assertEquals(FaultCategory.TIMEOUT to "handshake_timeout", outcome.fault.category to outcome.fault.code)
        assertEquals("timeout", host.expect<ErrorMessage>(skip = { it is Hello }).code)
    }

    @Test
    fun `a host that does not answer a handshake step is a retryable timeout and is told so`() = runBlocking {
        val host = connect()
        val outcome = link.exchangeCapabilities(decoders) as AttemptOutcome.Failed
        assertEquals(FaultCategory.TIMEOUT to "handshake_timeout", outcome.fault.category to outcome.fault.code)
        assertTrue(outcome.fault.isRetryable)
        assertEquals("timeout", host.expect<ErrorMessage>(skip = { it is Hello }).code)
    }

    @Test
    fun `a goodbye during the handshake is a retryable end of that attempt`() = runBlocking {
        val host = connect()
        val hostSide = thread {
            host.sendPreface()
            host.send(Bye("the session was stopped on the PC"))
        }
        val outcome = link.exchangeCapabilities(decoders) as AttemptOutcome.Failed
        hostSide.join(PATIENCE_MS)
        assertEquals("host_ended_handshake", outcome.fault.code)
        assertTrue(outcome.fault.isRetryable)
    }

    @Test
    fun `messages that arrive together in one read are all kept, and unknown control types are skipped`() = runBlocking {
        val connection = MemoryConnection()
        val host = connect(connection)
        val together = Protocol.PREFACE + ControlCodec.encode(Hello(0, 0, "host", "TEST-PC", "1.2.3")) + unknownControl()
        connection.toApp.output.write(together)
        val hostSide = thread {
            host.expect<Hello>()
            host.expect<ClientCapabilities>()
            host.send(TestHost.CONFIG_1080P60)
        }
        assertTrue(link.exchangeCapabilities(decoders) is AttemptOutcome.Succeeded)
        hostSide.join(PATIENCE_MS)
    }

    @Test
    fun `cancelling a blocked handshake step closes the socket and returns promptly`() = runBlocking {
        val host = connect()
        val step = async(Dispatchers.Default) { link.exchangeCapabilities(decoders) }
        host.expect<Hello>()
        val started = System.nanoTime()
        step.cancel()
        runCatching { step.await() }
        assertTrue((System.nanoTime() - started) / 1_000_000 < 500)
        assertTrue(host.closedWithin(PATIENCE_MS))
    }

    @Test
    fun `cancelling a wait for a host leaves the link listening, and the next wait gets the next host`() = runBlocking {
        val waiting = async(Dispatchers.Default) { link.awaitHost() }
        awaitListener()
        waiting.cancel()
        runCatching { waiting.await() }
        assertFalse(factory.listener.closed)

        connect()
        assertEquals(1, factory.listeners.size)
    }

    @Test
    fun `only adb may connect - anyone else is closed and not handed over`() = runBlocking {
        val stranger = MemoryConnection(peerUid = 10_123)
        val waiting = async(Dispatchers.Default) { link.awaitHost() }
        awaitListener().connect(stranger)
        awaitUntil { link.refusedPeers == 1 }
        assertTrue(stranger.closes > 0)
        assertFalse(waiting.isCompleted)

        awaitListener().connect(MemoryConnection())
        assertEquals(AttemptOutcome.Succeeded(Unit), withTimeout(PATIENCE_MS) { waiting.await() })
    }

    @Test
    fun `a newer connection replaces one still waiting to be taken`() = runBlocking {
        val waiting = async(Dispatchers.Default) { link.awaitHost() }
        val first = MemoryConnection()
        awaitListener().connect(first)
        withTimeout(PATIENCE_MS) { waiting.await() }

        // Accepted while nothing waits: the older one is replaced by the newer, and closed.
        link.endLink("next")
        val older = MemoryConnection()
        val newer = MemoryConnection()
        factory.listener.connect(older)
        factory.listener.connect(newer)
        awaitUntil { older.closes > 0 }
        val host = TestHost(newer)
        val hostSide = thread { host.handshake() }
        assertTrue(link.awaitHost() is AttemptOutcome.Succeeded)
        assertTrue(link.exchangeCapabilities(decoders) is AttemptOutcome.Succeeded)
        hostSide.join(PATIENCE_MS)
    }

    @Test
    fun `stopping listening closes the listener and a later wait listens again`() = runBlocking {
        connect()
        link.stopListening()
        assertTrue(factory.listener.closed)
        connect(listeners = 2)
        assertEquals(2, factory.listeners.size)
    }

    @Test
    fun `a socket that cannot be listened on is a terminal transport fault the user can act on`() = runBlocking {
        factory.failure = IOException("Address in use")
        val outcome = link.awaitHost() as AttemptOutcome.Failed
        assertEquals(FaultCategory.TRANSPORT to "listen_failed", outcome.fault.category to outcome.fault.code)
        assertEquals(FaultTreatment.TERMINAL, outcome.fault.treatment)
        assertTrue(outcome.fault.userActionable)
    }

    @Test
    fun `waiting for a new host discards the connection left from the last attempt`() = runBlocking {
        val first = MemoryConnection()
        connect(first)
        val second = async(Dispatchers.Default) { link.awaitHost() }
        awaitUntil { first.closes > 0 }
        factory.listener.connect(MemoryConnection())
        assertTrue(withTimeout(PATIENCE_MS) { second.await() } is AttemptOutcome.Succeeded)
    }

    @Test
    fun `waiting for the end before a stream was accepted is a contract violation`() = runBlocking {
        connect()
        assertNull(runCatching { link.awaitEnd() }.getOrNull())
        assertTrue(runCatching { link.awaitEnd() }.exceptionOrNull() is IllegalStateException)
    }

    private fun unknownControl(): ByteArray {
        val payload = "{}".toByteArray()
        val frame = ByteArray(Protocol.HEADER_LENGTH + payload.size)
        com.rsxdevelops.extendspace.protocol.FrameHeader(Protocol.CHANNEL_CONTROL, 99, 0, payload.size.toLong()).write(frame)
        payload.copyInto(frame, Protocol.HEADER_LENGTH)
        return frame
    }

    private fun awaitUntil(condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + PATIENCE_MS
        while (!condition()) {
            check(System.currentTimeMillis() < deadline) { "not reached in time" }
            Thread.sleep(1)
        }
    }
}
