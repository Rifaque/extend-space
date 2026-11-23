package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

// Every test runs the controller on the test's own thread: Dispatchers.Unconfined, fakes that
// complete inline, and a clock that moves only when the test moves it. No test sleeps.

class ClientSessionStartupTest {
    @Test
    fun `a clean start waits for a host, offers its decoders, chooses one, is ready before saying so, then streams`() {
        val harness = ClientHarness()

        assertEquals(WaitResult.STARTED, harness.start())

        assertEquals(
            listOf(
                "state Connecting",
                "link.awaitHost",
                "state Negotiating",
                "receiver.decoders",
                "link.exchange",
                "receiver.prepare",
                "receiver.start",
                "link.accept c2.hw.avc",
                "state Streaming",
                "link.awaitEnd",
            ),
            harness.log.entries,
        )
        assertEquals("c2.hw.avc", harness.link.offeredDecoders.single().name)
        assertEquals(FakeLink.stream, harness.receiver.stream)
        harness.assertSettled()
    }

    @Test
    fun `the stream's size is published while the session lasts and cleared when it ends`() {
        // D69: the session screen fits the picture to it.
        val harness = ClientHarness()
        assertEquals(null, harness.controller.stream.value)

        harness.start()
        assertEquals(StreamInfo(1920, 1080, 60, "DESKTOP"), harness.controller.stream.value)

        harness.controller.stop()
        harness.assertSettled()
        assertEquals(ConnectionState.Disconnected, harness.controller.state.value)
        assertEquals(null, harness.controller.stream.value)
    }

    @Test
    fun `the controller starts disconnected with no hosts, and as composed in the app fails at once without listening`() {
        val controller = ClientSessionController(
            UnavailableSessionLink(),
            UnavailableVideoReceiver(),
            ManualSessionClock(),
            CoroutineScope(SupervisorJob() + Dispatchers.Unconfined),
        )
        assertEquals(ConnectionState.Disconnected, controller.state.value)
        assertTrue(controller.hosts.value.isEmpty())

        assertEquals(WaitResult.STARTED, controller.waitForHost(TransportKind.USB))

        val failed = controller.state.value as ConnectionState.Failed
        assertEquals(ConnectionErrorCode.TRANSPORT_UNAVAILABLE, failed.error.code)
        assertEquals("transport_not_built", failed.error.cause!!.code)
    }

    @Test
    fun `each step that fails terminally ends the session after tearing down, with the code the user is told`() {
        data class Case(val step: String, val fault: SessionFault, val code: ConnectionErrorCode, val hostTold: Boolean)
        val cases = listOf(
            Case("awaitHost", ClientFaults.cannotListen, ConnectionErrorCode.TRANSPORT_UNAVAILABLE, true),
            Case("exchange", ClientFaults.versionMismatch, ConnectionErrorCode.INCOMPATIBLE_PROTOCOL, true),
            Case("prepare", ClientFaults.noDecoder, ConnectionErrorCode.DECODER_UNAVAILABLE, true),
            Case("accept", ClientFaults.malformed, ConnectionErrorCode.INCOMPATIBLE_PROTOCOL, true),
        )

        for (case in cases) {
            val harness = ClientHarness()
            when (case.step) {
                "awaitHost" -> harness.link.awaitHostBehaviour = { AttemptOutcome.Failed(case.fault) }
                "exchange" -> harness.link.exchangeBehaviour = { AttemptOutcome.Failed(case.fault) }
                "prepare" -> harness.receiver.prepareBehaviour = { AttemptOutcome.Failed(case.fault) }
                "accept" -> harness.link.acceptBehaviour = { AttemptOutcome.Failed(case.fault) }
            }

            harness.start()

            val failed = harness.state as ConnectionState.Failed
            assertEquals(case.step, case.code, failed.error.code)
            assertEquals(case.step, case.fault.code, failed.error.cause?.code)
            assertEquals(case.step, FaultTreatment.TERMINAL, failed.error.cause!!.treatment)
            assertFalse(case.step, harness.phases.contains("Streaming"))
            assertEquals(case.step, 1, harness.link.hostsAwaited)

            // Torn down once each, link first, then Failed last; the PC is told why when it can be.
            assertEquals(case.step, 1, harness.link.endLinks)
            assertEquals(case.step, case.fault.code, harness.link.lastEnd!!.second!!.code)
            assertEquals(case.step, listOf("link.endLink", "receiver.stop", "link.stopListening", "state Failed"), harness.log.from("link.endLink"))
            harness.assertSettled()
        }
    }

    @Test
    fun `a failed attempt is reported, waited out briefly, and the session goes on listening without listening twice`() {
        data class Case(val step: String, val fault: SessionFault)
        val cases = listOf(
            Case("awaitHost", ClientFaults.refusedPeer),
            Case("exchange", ClientFaults.handshakeTimeout),
            Case("accept", ClientFaults.hostClosed),
        )

        for (case in cases) {
            val harness = ClientHarness()
            when (case.step) {
                "awaitHost" -> harness.link.awaitHostBehaviour = { n -> if (n == 1) AttemptOutcome.Failed(case.fault) else AttemptOutcome.Succeeded(Unit) }
                "exchange" -> harness.link.exchangeBehaviour = { n -> if (n == 1) AttemptOutcome.Failed(case.fault) else AttemptOutcome.Succeeded(FakeLink.stream) }
                "accept" -> harness.link.acceptBehaviour = { n -> if (n == 1) AttemptOutcome.Failed(case.fault) else AttemptOutcome.Succeeded(Unit) }
            }

            harness.start()

            assertEquals(case.step, ConnectionState.Connecting(TransportKind.USB, case.fault), harness.state)
            harness.clock.advance(249)
            assertEquals(case.step, 1, harness.link.hostsAwaited)
            harness.clock.advance(1)
            assertEquals(case.step, "Streaming", harness.phase)
            assertEquals(case.step, 2, harness.link.hostsAwaited)
            assertEquals(case.step, 1, harness.link.listenStarts)

            // A receiver started for a stream the link then failed to accept is stopped again.
            assertEquals(case.step, if (case.step == "accept") 1 else 0, harness.receiver.stops)
            assertEquals(case.step, 0, harness.link.endLinks)
            harness.assertSettled()
        }
    }

    @Test
    fun `before it has ever streamed the session waits for a host for as long as the user lets it`() {
        val harness = ClientHarness()
        harness.link.awaitHostBehaviour = { AttemptOutcome.Failed(ClientFaults.refusedPeer) }
        harness.start()

        harness.clock.advance(10 * 60 * 1_000)

        assertEquals("Connecting", harness.phase)
        assertEquals(1 + 10 * 60 * 4, harness.link.hostsAwaited)
    }

    @Test
    fun `a cancellation nobody asked for ends the session as internal and is not retried`() {
        val harness = ClientHarness()
        harness.link.exchangeBehaviour = { AttemptOutcome.Cancelled }

        harness.start()
        harness.clock.advance(60_000)

        val failed = harness.state as ConnectionState.Failed
        assertEquals(ConnectionErrorCode.INTERNAL, failed.error.code)
        assertEquals(SessionFaultCodes.UNREQUESTED_CANCELLATION, failed.error.cause!!.code)
        assertEquals(1, harness.link.hostsAwaited)
        harness.assertSettled()
    }

    @Test
    fun `a seam that throws ends the session with the exception kept`() {
        val harness = ClientHarness()
        harness.receiver.prepareBehaviour = { throw IllegalStateException("the codec list was empty") }

        harness.start()

        val failed = harness.state as ConnectionState.Failed
        assertEquals(ConnectionErrorCode.INTERNAL, failed.error.code)
        assertEquals("IllegalStateException: the codec list was empty", failed.error.cause!!.message)
        harness.assertSettled()
    }
}

class ClientSessionStopTest {
    private fun arrange(harness: ClientHarness, point: String) {
        when (point) {
            "awaitHost" -> harness.link.awaitHostBehaviour = { blockedUntilCancelled(harness.log, "accept cancelled: socket closed") }
            "exchange" -> harness.link.exchangeBehaviour = { blockedUntilCancelled(harness.log, "read cancelled: socket closed") }
            "prepare" -> harness.receiver.prepareBehaviour = { blockedUntilCancelled(harness.log, "prepare cancelled") }
            "accept" -> harness.link.acceptBehaviour = { blockedUntilCancelled(harness.log, "write cancelled: socket closed") }
            "retry pause" -> harness.link.awaitHostBehaviour = { AttemptOutcome.Failed(ClientFaults.refusedPeer) }
        }

        assertEquals(WaitResult.STARTED, harness.start())

        if (point == "waiting for the host to return") {
            harness.link.awaitHostBehaviour = { blockedUntilCancelled(harness.log, "accept cancelled: socket closed") }
            harness.link.end(LinkEnd(LinkEndKind.LOST, ClientFaults.hostClosed))
            assertEquals("Reconnecting", harness.phase)
        }
    }

    @Test
    fun `stop at any point ends disconnected, unblocks what was waiting, tears down once each and starts nothing after`() {
        for (point in listOf("awaitHost", "exchange", "prepare", "accept", "retry pause", "streaming", "waiting for the host to return")) {
            val harness = ClientHarness()
            arrange(harness, point)

            assertTrue(point, harness.controller.stop())

            assertEquals(point, "Disconnected", harness.phase)
            assertNull(point, (harness.state as? ConnectionState.Failed)?.error)
            assertEquals(point, listOf("Disconnecting", "Disconnected"), harness.phases.takeLast(2))

            // What was blocked was released by its cancellation before the link was ended.
            val unblocked = harness.log.entries.indexOfFirst { it.endsWith("cancelled: socket closed") || it == "prepare cancelled" }
            if (point !in listOf("retry pause", "streaming")) {
                assertTrue(point, unblocked in 0 until harness.log.indexOf("link.endLink"))
            }

            val after = harness.log.from("state Disconnecting")
            assertTrue(point + ": " + after, after.all { it.startsWith("state ") || it.startsWith("link.endLink") || it == "receiver.stop" || it == "link.stopListening" || it.contains("cancelled") || it == "link ended: CLOSED" })
            assertEquals(point, 1, harness.log.count("link.endLink"))
            assertEquals(point, 1, harness.log.count("link.stopListening"))

            // Cancellation is not failure: nothing more is attempted, however long one waits.
            val attempts = harness.link.hostsAwaited
            harness.clock.advance(10 * 60 * 1_000)
            assertEquals(point, attempts, harness.link.hostsAwaited)
            assertTrue(point, harness.controller.isReleased)
            assertTrue(point, harness.controller.teardownFaults.isEmpty())
            harness.assertSettled()
        }
    }

    @Test
    fun `stop returns at once even when the link is stuck, and a second stop or a stop with nothing to stop is refused`() {
        val harness = ClientHarness()
        assertFalse(harness.controller.stop())

        val gate = CompletableDeferred<Unit>()
        harness.link.endLinkBehaviour = { stuckUntil(gate) }
        harness.streaming()

        assertTrue(harness.controller.stop())
        assertEquals("Disconnecting", harness.phase)
        assertFalse(harness.controller.stop())
        assertEquals(1, harness.link.endLinks)
        gate.complete(Unit)
        assertEquals("Disconnected", harness.phase)
    }

    @Test
    fun `waiting for a host while a session exists is refused and starts nothing`() {
        val harness = ClientHarness()
        harness.link.awaitHostBehaviour = { blockedUntilCancelled(harness.log, "accept cancelled") }
        harness.start()

        assertEquals(WaitResult.ALREADY_ACTIVE, harness.start())
        assertEquals(1, harness.link.hostsAwaited)
        assertEquals(1, harness.link.listenStarts)
    }

    @Test
    fun `a failure must be acknowledged before the next session, which inherits nothing`() {
        val harness = ClientHarness()
        harness.link.exchangeBehaviour = { n -> if (n == 1) AttemptOutcome.Failed(ClientFaults.versionMismatch) else AttemptOutcome.Succeeded(FakeLink.stream) }
        harness.start()
        assertEquals("Failed", harness.phase)

        assertEquals(WaitResult.ALREADY_ACTIVE, harness.start())
        assertFalse(harness.controller.stop())
        assertTrue(harness.controller.acknowledgeError())
        harness.streaming()
        assertEquals(ConnectionState.Streaming(TransportKind.USB), harness.state)
    }

    @Test
    fun `a call that ignores cancellation is abandoned at the deadline, and no new session starts until it has returned and been released`() {
        val harness = ClientHarness()
        val gate = CompletableDeferred<AttemptOutcome<Unit>>()
        harness.link.awaitHostBehaviour = { stuckUntil(gate) }
        harness.start()

        harness.controller.stop()
        harness.clock.advance(3_999)
        assertEquals("Disconnecting", harness.phase)
        assertEquals(0, harness.link.endLinks)

        harness.clock.advance(1)
        assertEquals("Disconnected", harness.phase)

        // The rest of teardown still ran, and the overrun is on record.
        assertEquals(1, harness.link.endLinks)
        val overran = harness.controller.teardownFaults.single()
        assertEquals(SessionFaultCodes.TEARDOWN_STEP_OVERRAN, overran.code)
        assertFalse(harness.controller.isReleased)
        assertEquals(WaitResult.PREVIOUS_SESSION_NOT_RELEASED, harness.start())
        assertEquals("Disconnected", harness.phase)

        // It returns having accepted a host: the link is ended and listening stopped again before
        // anything new may start.
        gate.complete(AttemptOutcome.Succeeded(Unit))
        assertEquals(2, harness.link.endLinks)
        assertFalse(harness.link.listening)
        assertTrue(harness.controller.isReleased)
        assertEquals(0, harness.link.exchanges)
        assertEquals(WaitResult.STARTED, harness.start())
        assertEquals("Streaming", harness.phase)
    }

    @Test
    fun `a teardown step that never returns is abandoned and the steps after it still run`() {
        val harness = ClientHarness()
        val gate = CompletableDeferred<Unit>()
        harness.receiver.stopBehaviour = { stuckUntil(gate) }
        harness.streaming()

        harness.controller.stop()
        harness.clock.advance(4_000)

        assertEquals("Disconnected", harness.phase)
        assertEquals(1, harness.log.count("link.stopListening"))
        assertTrue(harness.controller.teardownFaults.single().message.contains("stop the receiver"))
        assertEquals(WaitResult.PREVIOUS_SESSION_NOT_RELEASED, harness.start())

        gate.complete(Unit)
        assertEquals(WaitResult.STARTED, harness.start())
    }

    @Test
    fun `a stop during a failure's teardown ends the session as stopped, not failed`() {
        val harness = ClientHarness()
        val gate = CompletableDeferred<Unit>()
        harness.link.endLinkBehaviour = { stuckUntil(gate) }
        harness.streaming()

        harness.receiver.fail(ClientFaults.decoderLost)
        assertEquals("Streaming", harness.phase)

        assertTrue(harness.controller.stop())
        gate.complete(Unit)

        assertEquals("Disconnected", harness.phase)
        assertEquals(1, harness.link.endLinks)
        harness.assertSettled()
    }

    @Test
    fun `a teardown step that throws is recorded and teardown carries on`() {
        val harness = ClientHarness()
        harness.link.endLinkBehaviour = { throw IllegalStateException("socket already closed") }
        harness.streaming()

        harness.controller.stop()

        assertEquals("Disconnected", harness.phase)
        assertEquals(SessionFaultCodes.TEARDOWN_STEP_FAILED, harness.controller.teardownFaults.single().code)
        assertEquals(1, harness.log.count("link.stopListening"))
        assertTrue(harness.controller.isReleased)
    }
}

class ClientSessionStreamingTest {
    @Test
    fun `a lost host that comes back within the wait is served again on the same listener`() {
        val harness = ClientHarness().streaming()

        harness.link.end(LinkEnd(LinkEndKind.LOST, ClientFaults.hostClosed))

        // Back to waiting at once (there is nothing to back off from on this side), and the fake
        // host came straight back. Why it was lost was visible while reconnecting, then cleared.
        assertTrue(harness.states.contains(ConnectionState.Reconnecting(TransportKind.USB, ClientFaults.hostClosed)))
        assertEquals(ConnectionState.Streaming(TransportKind.USB), harness.state)
        assertEquals(1, harness.receiver.stops)
        assertEquals(2, harness.receiver.starts)
        assertEquals(2, harness.link.hostsAwaited)
        assertEquals(1, harness.link.listenStarts)
        assertEquals(0, harness.link.endLinks)
        harness.assertSettled()
    }

    @Test
    fun `a lost host that does not come back within 30 s ends the session as connection lost`() {
        val harness = ClientHarness().streaming()
        harness.link.awaitHostBehaviour = { blockedUntilCancelled(harness.log, "accept cancelled: socket closed") }

        harness.link.end(LinkEnd(LinkEndKind.LOST, ClientFaults.hostClosed))
        harness.clock.advance(29_999)
        assertEquals("Reconnecting", harness.phase)

        harness.clock.advance(1)
        val failed = harness.state as ConnectionState.Failed
        assertEquals(ConnectionErrorCode.CONNECTION_LOST, failed.error.code)
        assertEquals(ClientFaults.hostClosed, failed.error.cause)
        assertEquals("The PC did not reconnect within 30 s.", failed.error.detail)
        assertTrue(harness.log.entries.contains("accept cancelled: socket closed"))

        // Nobody to tell.
        assertNull(harness.link.lastEnd!!.second)
        harness.assertSettled()
    }

    @Test
    fun `while waiting for a lost host, failed attempts are reported and the last one is the cause if it never returns`() {
        val harness = ClientHarness().streaming()
        harness.link.awaitHostBehaviour = { AttemptOutcome.Failed(ClientFaults.refusedPeer) }

        harness.link.end(LinkEnd(LinkEndKind.LOST, ClientFaults.hostClosed))
        assertEquals(ConnectionState.Reconnecting(TransportKind.USB, ClientFaults.refusedPeer), harness.state)

        harness.clock.advance(30_000)
        assertEquals(ClientFaults.refusedPeer, (harness.state as ConnectionState.Failed).error.cause)
        harness.assertSettled()
    }

    @Test
    fun `streaming again gives the next loss a whole new wait`() {
        val harness = ClientHarness().streaming()
        var refuse = true
        harness.link.awaitHostBehaviour = { if (refuse) AttemptOutcome.Failed(ClientFaults.refusedPeer) else AttemptOutcome.Succeeded(Unit) }

        harness.link.end(LinkEnd(LinkEndKind.LOST, ClientFaults.hostClosed))
        harness.clock.advance(25_000)
        refuse = false
        harness.clock.advance(250)
        assertEquals("Streaming", harness.phase)

        refuse = true
        harness.link.end(LinkEnd(LinkEndKind.LOST, ClientFaults.hostClosed))
        harness.clock.advance(29_999)
        assertEquals("Reconnecting", harness.phase)
        harness.clock.advance(1)
        assertEquals("Failed", harness.phase)
    }

    @Test
    fun `the PC saying goodbye ends the session without error and stops listening`() {
        val harness = ClientHarness().streaming()

        harness.link.end(LinkEnd(LinkEndKind.PEER_ENDED, peerReason = "stopped on the PC"))

        assertEquals(listOf("Disconnecting", "Disconnected"), harness.phases.takeLast(2))
        assertEquals(1, harness.link.hostsAwaited)
        harness.assertSettled()
    }

    @Test
    fun `a fatal end of the link and a receiver that cannot go on both fail the session`() {
        val fatal = ClientHarness().streaming()
        fatal.link.end(LinkEnd(LinkEndKind.FATAL, ClientFaults.malformed))
        assertEquals(ConnectionErrorCode.INCOMPATIBLE_PROTOCOL, (fatal.state as ConnectionState.Failed).error.code)
        fatal.assertSettled()

        val decoder = ClientHarness().streaming()
        decoder.receiver.fail(ClientFaults.decoderLost)
        assertEquals(ConnectionErrorCode.DECODER_UNAVAILABLE, (decoder.state as ConnectionState.Failed).error.code)
        assertEquals(ClientFaults.decoderLost, decoder.link.lastEnd!!.second)
        decoder.assertSettled()
    }

    @Test
    fun `a decoder that fails while running is recovered by the receiver asking for a keyframe, which reaches the PC`() {
        val harness = ClientHarness().streaming()

        harness.receiver.onKeyframeNeeded!!("decode_error")
        harness.receiver.onKeyframeNeeded!!("sequence_gap")

        assertEquals("Streaming", harness.phase)
        assertEquals(listOf("decode_error", "sequence_gap"), harness.link.keyframesSent)
    }
}

class ClientSessionOutputTest {
    @Test
    fun `an output lent before the stream starts is used, and a keyframe is asked for when they meet`() {
        val harness = ClientHarness()
        val surface = TestOutput("surface 1")

        harness.controller.lendOutput(surface)
        harness.streaming()

        assertSame(surface, harness.receiver.output)
        assertEquals(listOf("startup"), harness.link.keyframesSent)
        assertTrue(harness.log.indexOf("link.keyframe startup") > harness.log.indexOf("state Streaming"))
    }

    @Test
    fun `withdrawing releases the decoder before returning and the controller keeps nothing of the output`() {
        val harness = ClientHarness()
        val surface = TestOutput("surface 1")
        harness.controller.lendOutput(surface)
        harness.streaming()

        harness.controller.withdrawOutput(surface)

        assertEquals("receiver.output none", harness.log.entries.last())
        assertNull(harness.receiver.output)
        assertNull(harness.controller.output)

        // A session ending does not touch the output either way.
        harness.controller.stop()
        assertNull(harness.receiver.output)
    }

    @Test
    fun `a replaced surface, as on rotation, keeps the session and asks for a keyframe for the new one`() {
        val harness = ClientHarness()
        val first = TestOutput("surface 1")
        val second = TestOutput("surface 2")
        harness.controller.lendOutput(first)
        harness.streaming()

        harness.controller.withdrawOutput(first)
        harness.clock.advance(1_000)
        harness.controller.lendOutput(second)

        assertEquals("Streaming", harness.phase)
        assertSame(second, harness.receiver.output)
        assertEquals(listOf("startup", "surface_recreated"), harness.link.keyframesSent)

        // The grace period started by the withdrawal was ended by the lending.
        harness.clock.advance(60_000)
        assertEquals("Streaming", harness.phase)
    }

    @Test
    fun `a new screen lending before the old one withdraws keeps the new output`() {
        val harness = ClientHarness()
        val old = TestOutput("old surface")
        val new = TestOutput("new surface")
        harness.controller.lendOutput(old)
        harness.streaming()

        harness.controller.lendOutput(new)
        harness.controller.withdrawOutput(old)

        assertSame(new, harness.receiver.output)
        assertSame(new, harness.controller.output)
        assertEquals(0, harness.log.count("receiver.output none"))
    }

    @Test
    fun `a streaming session with no output for the grace period says goodbye and stops listening, not as an error`() {
        val harness = ClientHarness()
        val surface = TestOutput("surface 1")
        harness.controller.lendOutput(surface)
        harness.streaming()

        harness.controller.withdrawOutput(surface)
        harness.clock.advance(29_999)
        assertEquals("Streaming", harness.phase)

        harness.clock.advance(1)
        assertEquals(listOf("Disconnecting", "Disconnected"), harness.phases.takeLast(2))
        assertTrue(harness.link.lastEnd!!.first.startsWith("nothing has been shown on the device"))
        assertNull(harness.link.lastEnd!!.second)
        harness.assertSettled()
    }

    @Test
    fun `the grace period also applies to a stream that started with no output at all`() {
        val harness = ClientHarness().streaming()

        harness.clock.advance(30_000)

        assertEquals("Disconnected", harness.phase)
        assertTrue(harness.link.keyframesSent.isEmpty())
    }

    @Test
    fun `an output kept across a lost and returning host gets a keyframe for each new stream`() {
        val harness = ClientHarness()
        harness.controller.lendOutput(TestOutput("surface 1"))
        harness.streaming()

        harness.link.end(LinkEnd(LinkEndKind.LOST, ClientFaults.hostClosed))

        assertEquals("Streaming", harness.phase)
        assertEquals(listOf("startup", "startup"), harness.link.keyframesSent)
        assertEquals(2, harness.receiver.starts)
    }
}
