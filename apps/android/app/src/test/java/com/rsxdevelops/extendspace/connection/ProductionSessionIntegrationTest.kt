package com.rsxdevelops.extendspace.connection

import com.rsxdevelops.extendspace.link.DisplayInfo
import com.rsxdevelops.extendspace.link.MemoryConnection
import com.rsxdevelops.extendspace.link.MemoryListenerFactory
import com.rsxdevelops.extendspace.link.PATIENCE_MS
import com.rsxdevelops.extendspace.link.TestHost
import com.rsxdevelops.extendspace.link.UsbHostLink
import com.rsxdevelops.extendspace.link.UsbHostLinkOptions
import com.rsxdevelops.extendspace.protocol.Bye
import com.rsxdevelops.extendspace.protocol.ClientStats
import com.rsxdevelops.extendspace.protocol.ErrorMessage
import com.rsxdevelops.extendspace.protocol.KeyframeRequest
import com.rsxdevelops.extendspace.protocol.Ping
import com.rsxdevelops.extendspace.protocol.Pong
import com.rsxdevelops.extendspace.protocol.StreamReady
import com.rsxdevelops.extendspace.video.FakeOutput
import com.rsxdevelops.extendspace.video.FakePlatform
import com.rsxdevelops.extendspace.video.H264Samples
import com.rsxdevelops.extendspace.video.HostClockEstimate
import com.rsxdevelops.extendspace.video.MediaCodecVideoReceiver
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * The client as the application composes it, minus the hardware: the real
 * [ClientSessionController] on a one-at-a-time dispatcher, the real [UsbHostLink] over in-memory
 * sockets, and the real [MediaCodecVideoReceiver] over a scripted decoder platform, against a
 * scripted host. These show that a keyframe from the host reaches a decoder on the lent output, that
 * the output can come and go during a stream, that losing and regaining the host restarts decoding
 * at a keyframe, and that stopping leaves no decoder and no listener. They do not show a real
 * decoder, a real surface or a device.
 */
class ProductionSessionIntegrationTest {
    private val factory = MemoryListenerFactory()
    private val platform = FakePlatform()
    private val hostClock = HostClockEstimate()
    private val receiver = MediaCodecVideoReceiver(platform, hostClock)
    private val link = UsbHostLink(
        factory,
        "Fake Pad",
        { "9.9.9" },
        { DisplayInfo(3200, 2136, 144.0) },
        receiver::stats,
        hostClock,
        UsbHostLinkOptions(keepaliveMillis = 2_000, statsIntervalMillis = 100),
    )
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default.limitedParallelism(1))
    private val controller = ClientSessionController(
        link,
        receiver,
        SystemSessionClock(),
        scope,
        ClientSessionOptions(hostReturnMillis = 3_000, outputGraceMillis = 30_000, teardownDeadlineMillis = TEARDOWN_DEADLINE_MS),
    )

    private companion object {
        const val TEARDOWN_DEADLINE_MS = 4_000L

        /** Scheduling on a busy test machine; far less than any hang these tests hold open. */
        const val DEADLINE_SLACK_MS = 1_500L

        /** The longest a test holds a decoder operation open: far past the deadline, and bounded so that a broken build fails instead of hanging. */
        const val HOLD_MS = 2 * PATIENCE_MS
    }

    @After
    fun tearDown() {
        if (controller.stop()) awaitState { it is ConnectionState.Disconnected }
        scope.cancel()
    }

    /** The UI asks to wait; a host connects and completes the handshake. */
    private fun streamingHost(): TestHost {
        if (controller.state.value is ConnectionState.Disconnected) assertEquals(WaitResult.STARTED, controller.waitForHost(TransportKind.USB))
        val connection = MemoryConnection()
        awaitUntil { synchronized(factory.listeners) { factory.listeners.isNotEmpty() } }
        factory.listener.connect(connection)
        val host = TestHost(connection)
        host.handshake()
        assertEquals("c2.fake.avc.decoder", host.expect<StreamReady>().decoder)
        awaitState { it is ConnectionState.Streaming }
        return host
    }

    @Test
    fun `a keyframe reaches a decoder on the lent output, and the output lent late asks the host for one`() {
        val host = streamingHost()

        // No output yet: the host's first keyframe is received and discarded, nothing is created.
        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        awaitUntil { receiver.stats()?.framesDiscarded == 1L }
        assertTrue(platform.created.isEmpty())

        // The screen lends its surface: the client asks for a keyframe at once.
        val output = FakeOutput("surface")
        controller.lendOutput(output)
        assertEquals(KeyframeRequest("surface_recreated"), host.expect<KeyframeRequest>())

        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        host.sendVideo(H264Samples.delta, 0)
        awaitUntil { platform.created.singleOrNull()?.submitted?.size == 2 }
        assertTrue(platform.created.single().output === output)

        // The host hears how many frames were submitted.
        awaitUntil { (host.next { it !is ClientStats } as ClientStats).framesSubmitted == 2L }
    }

    @Test
    fun `withdrawing the output during a stream releases the decoder before returning, and lending again asks for a keyframe`() {
        val output = FakeOutput("first")
        controller.lendOutput(output)
        val host = streamingHost()
        assertEquals(KeyframeRequest("startup"), host.expect<KeyframeRequest>())
        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        // In use, not only made: the owner publishes it just after the platform has created it (D59).
        awaitUntil { platform.created.size == 1 && receiver.hasDecoder }

        controller.withdrawOutput(output)
        assertEquals(1, platform.created.single().released)

        Thread.sleep(600) // past the link's half-second limit on requests
        controller.lendOutput(FakeOutput("second"))
        assertEquals(KeyframeRequest("surface_recreated"), host.expect<KeyframeRequest>())
        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        awaitUntil { platform.created.size == 2 }
        assertEquals(1, platform.live.size)
        assertEquals(ConnectionState.Streaming(TransportKind.USB), controller.state.value)
    }

    @Test
    fun `a host lost and back within the wait restarts decoding at a keyframe on the same output`() {
        val output = FakeOutput("surface")
        controller.lendOutput(output)
        val first = streamingHost()
        first.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        awaitUntil { platform.created.size == 1 }

        first.connection.close()
        awaitState { it is ConnectionState.Reconnecting }
        awaitUntil { platform.created.single().released == 1 }

        val second = streamingHost()
        assertEquals(KeyframeRequest("startup"), second.expect<KeyframeRequest>())
        second.sendVideo(H264Samples.delta, 0)
        second.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        awaitUntil { platform.created.size == 2 }
        assertTrue(platform.created[1].output === output)
        assertEquals(listOf(true), platform.created[1].submitted.map { it.second })
    }

    @Test
    fun `the host saying goodbye ends the session without error and leaves no decoder`() {
        controller.lendOutput(FakeOutput("surface"))
        val host = streamingHost()
        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        awaitUntil { platform.created.size == 1 }

        host.send(Bye("the session was stopped on the PC"))
        awaitState { it is ConnectionState.Disconnected }
        assertTrue(platform.live.isEmpty())
        assertTrue(controller.isReleased)
        assertTrue(factory.listener.closed)
    }

    @Test
    fun `stopping on the device says goodbye, stops listening and leaves no decoder`() {
        controller.lendOutput(FakeOutput("surface"))
        val host = streamingHost()
        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        awaitUntil { platform.created.size == 1 }

        assertTrue(controller.stop())
        assertEquals("the session was stopped on the device", host.expect<Bye> { it is ClientStats || it is KeyframeRequest || it is com.rsxdevelops.extendspace.protocol.Pong }.reason)
        awaitState { it is ConnectionState.Disconnected }
        assertTrue(platform.live.isEmpty())
        assertTrue(factory.listener.closed)
        assertTrue(controller.isReleased)
        assertTrue(controller.teardownFaults.isEmpty())
    }

    @Test
    fun `a decoder that cannot be created ends the session failed and the host is told the stream was rejected`() {
        controller.lendOutput(FakeOutput("surface"))
        val host = streamingHost()
        platform.createFailure = IllegalStateException("no codec instance left")
        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)

        val error = host.expect<ErrorMessage> { it is ClientStats || it is KeyframeRequest || it is com.rsxdevelops.extendspace.protocol.Pong }
        assertEquals("stream_config_rejected" to true, error.code to error.fatal)
        val failed = awaitState { it is ConnectionState.Failed } as ConnectionState.Failed
        assertEquals(ConnectionErrorCode.DECODER_UNAVAILABLE, failed.error.code)
        assertTrue(controller.acknowledgeError())
    }

    @Test
    fun `a recovery request right after the startup request reaches the host`() {
        // D61: the startup request at Streaming used to close the link's 500 ms window, so a
        // sequence gap moments later asked into it and was dropped; with no further frame, never asked again.
        controller.lendOutput(FakeOutput("surface"))
        val host = streamingHost()
        assertEquals(KeyframeRequest("startup"), host.expect<KeyframeRequest>())
        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS, sequenceNumber = 0)
        host.sendVideo(H264Samples.delta, 0, sequenceNumber = 5)
        assertEquals(KeyframeRequest("sequence_gap"), host.expect<KeyframeRequest>())
    }

    @Test
    fun `a decoder being created does not hold up the link - a ping is answered and statistics go on while frames wait for it`() {
        controller.lendOutput(FakeOutput("surface"))
        val host = streamingHost()
        val creating = CountDownLatch(1)
        val proceed = CountDownLatch(1)
        platform.duringCreate = {
            creating.countDown()
            proceed.await(HOLD_MS, TimeUnit.MILLISECONDS)
        }
        try {
            host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
            assertTrue(creating.await(PATIENCE_MS, TimeUnit.MILLISECONDS))
            host.sendVideo(H264Samples.delta, 0)

            // The reader is free (until D59 it was inside the creation), and so is the writer.
            host.send(Ping(42, 1_000, null, null))
            assertEquals(42L, host.expect<Pong> { it is ClientStats || it is KeyframeRequest }.id)
            awaitUntil { (host.next { it !is ClientStats } as ClientStats).queueDepth == 2 }
            assertEquals(1L, proceed.count)
        } finally {
            proceed.countDown()
        }
        awaitUntil { platform.created.singleOrNull()?.submitted?.size == 2 }
        assertEquals(listOf(true, false), platform.created.single().submitted.map { it.second })
    }

    @Test
    fun `a decoder creation that does not return cannot hold teardown past its deadline, and the next session waits for it`() {
        controller.lendOutput(FakeOutput("surface"))
        val host = streamingHost()
        val creating = CountDownLatch(1)
        val hung = CountDownLatch(1)
        platform.duringCreate = {
            creating.countDown()
            hung.await(HOLD_MS, TimeUnit.MILLISECONDS)
        }
        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        assertTrue(creating.await(PATIENCE_MS, TimeUnit.MILLISECONDS))

        try {
            assertStopEndsByTheDeadline()
        } finally {
            hung.countDown()
        }
        // The creation returns into a stopped stream: whatever it made is released, and only then
        // may a new session begin.
        awaitUntil { controller.isReleased }
        assertTrue(platform.live.isEmpty())
        assertTrue(factory.listener.closed)
    }

    @Test
    fun `a decoder release that does not return cannot hold teardown past its deadline, and the next session waits for it`() {
        controller.lendOutput(FakeOutput("surface"))
        val host = streamingHost()
        host.sendVideo(H264Samples.keyframe(), H264Samples.KEYFRAME_FLAGS)
        awaitUntil { platform.created.size == 1 }
        val hung = CountDownLatch(1)
        platform.duringRelease = { hung.await(HOLD_MS, TimeUnit.MILLISECONDS) }

        try {
            assertStopEndsByTheDeadline()
        } finally {
            hung.countDown()
        }
        awaitUntil { controller.isReleased }
        assertTrue(platform.live.isEmpty())
        assertTrue(factory.listener.closed)
    }

    /**
     * Stops on the device while the receiver is held up, and checks the teardown contract: the
     * session ends by the deadline with the overrun recorded, and no new session may start while
     * the receiver's step is still running.
     */
    private fun assertStopEndsByTheDeadline() {
        val stoppedNanos = System.nanoTime()
        assertTrue(controller.stop())
        awaitState { it is ConnectionState.Disconnected }
        val tookMillis = (System.nanoTime() - stoppedNanos) / 1_000_000
        assertTrue("teardown took $tookMillis ms against a $TEARDOWN_DEADLINE_MS ms deadline", tookMillis < TEARDOWN_DEADLINE_MS + DEADLINE_SLACK_MS)
        // The receiver's step overran; the steps after it start past the deadline and are recorded as left running too.
        val faults = controller.teardownFaults
        val described = faults.joinToString { "${it.code}: ${it.message}" }
        assertTrue(described, faults.isNotEmpty() && faults.all { it.code == SessionFaultCodes.TEARDOWN_STEP_OVERRAN })
        assertTrue(described, faults.first().message.contains("'stop the receiver'"))
        assertFalse(controller.isReleased)
        assertEquals(WaitResult.PREVIOUS_SESSION_NOT_RELEASED, controller.waitForHost(TransportKind.USB))
    }

    private fun awaitState(condition: (ConnectionState) -> Boolean): ConnectionState {
        awaitUntil { condition(controller.state.value) }
        return controller.state.value
    }

    private fun awaitUntil(condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + PATIENCE_MS
        while (!condition()) {
            check(System.currentTimeMillis() < deadline) { "not reached in time; state ${controller.state.value}" }
            Thread.sleep(2)
        }
    }
}
