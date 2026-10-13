package com.rsxdevelops.extendspace.livestream

import com.rsxdevelops.extendspace.livestream.protocol.ClientStats
import com.rsxdevelops.extendspace.livestream.protocol.LatencySummary
import com.rsxdevelops.extendspace.livestream.protocol.Protocol
import com.rsxdevelops.extendspace.livestream.protocol.StreamConfig
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers the decisions and arithmetic of the live path. The socket, the decoder and the picture
 * itself are verified on a device, not here.
 */
class LiveLogicTest {
    private val picture = 0
    private val keyframe = Protocol.FLAG_KEYFRAME or Protocol.FLAG_CONFIG

    @Test
    fun `nothing is decoded before the first keyframe that carries its configuration`() {
        val gate = VideoGate()
        assertTrue(gate.needsKeyframe)

        assertEquals(GateDecision.DISCARD_AWAITING_KEYFRAME, gate.onFrame(0, picture, hasSurface = true))
        assertEquals(GateDecision.DISCARD_AWAITING_KEYFRAME, gate.onFrame(1, Protocol.FLAG_KEYFRAME, hasSurface = true))
        assertEquals(GateDecision.DECODE, gate.onFrame(2, keyframe, hasSurface = true))
        assertEquals(GateDecision.DECODE, gate.onFrame(3, picture, hasSurface = true))

        assertFalse(gate.needsKeyframe)
        assertEquals(0, gate.framesMissing)
    }

    @Test
    fun `a gap in the sequence stops decoding until the next keyframe and counts what was lost`() {
        val gate = startedGate()

        assertEquals(GateDecision.DISCARD_AWAITING_KEYFRAME, gate.onFrame(5, picture, hasSurface = true))
        assertEquals(4, gate.framesMissing)
        assertEquals(GateDecision.DISCARD_AWAITING_KEYFRAME, gate.onFrame(6, picture, hasSurface = true))
        assertEquals(GateDecision.DECODE, gate.onFrame(7, keyframe, hasSurface = true))
        assertEquals(GateDecision.DECODE, gate.onFrame(8, picture, hasSurface = true))
        assertEquals(4, gate.framesMissing)
    }

    @Test
    fun `a keyframe right after a gap restarts decoding at once`() {
        val gate = startedGate()

        assertEquals(GateDecision.DECODE, gate.onFrame(10, keyframe, hasSurface = true))

        assertEquals(9, gate.framesMissing)
        assertFalse(gate.needsKeyframe)
    }

    @Test
    fun `without a surface frames are discarded and a keyframe is needed when it returns`() {
        val gate = startedGate()

        assertEquals(GateDecision.DISCARD_NO_SURFACE, gate.onFrame(1, picture, hasSurface = false))
        assertEquals(GateDecision.DISCARD_NO_SURFACE, gate.onFrame(2, keyframe, hasSurface = false))
        assertEquals(GateDecision.DISCARD_AWAITING_KEYFRAME, gate.onFrame(3, picture, hasSurface = true))
        assertEquals(GateDecision.DECODE, gate.onFrame(4, keyframe, hasSurface = true))
        assertEquals(0, gate.framesMissing)
    }

    @Test
    fun `a decoder failure breaks the chain`() {
        val gate = startedGate()

        gate.breakChain()

        assertTrue(gate.needsKeyframe)
        assertEquals(GateDecision.DISCARD_AWAITING_KEYFRAME, gate.onFrame(1, picture, hasSurface = true))
        assertEquals(GateDecision.DECODE, gate.onFrame(2, keyframe, hasSurface = true))
    }

    @Test
    fun `sequence numbers wrap at two to the thirty-second`() {
        val gate = VideoGate()
        gate.onFrame(0xFFFF_FFFEL, keyframe, hasSurface = true)

        assertEquals(GateDecision.DECODE, gate.onFrame(0xFFFF_FFFFL, picture, hasSurface = true))
        assertEquals(GateDecision.DECODE, gate.onFrame(0, picture, hasSurface = true))
        assertEquals(GateDecision.DECODE, gate.onFrame(1, picture, hasSurface = true))
        assertEquals(0, gate.framesMissing)

        // Two lost across the wrap.
        val lossy = VideoGate()
        lossy.onFrame(0xFFFF_FFFFL, keyframe, hasSurface = true)
        lossy.onFrame(2, picture, hasSurface = true)
        assertEquals(2, lossy.framesMissing)
    }

    @Test
    fun `a sequence that jumps backwards is a restart, not billions of lost frames`() {
        val gate = startedGate()
        gate.onFrame(1, picture, hasSurface = true)
        gate.onFrame(2, picture, hasSurface = true)

        assertEquals(GateDecision.DISCARD_AWAITING_KEYFRAME, gate.onFrame(0, picture, hasSurface = true))

        assertEquals(0, gate.framesMissing)
        assertTrue(gate.needsKeyframe)
    }

    @Test
    fun `a run of discarded frames asks for a keyframe once per interval`() {
        val limiter = RequestLimiter(minimumIntervalMicros = 500_000)

        assertTrue(limiter.tryAcquire(1_000_000))
        assertFalse(limiter.tryAcquire(1_000_001))
        assertFalse(limiter.tryAcquire(1_499_999))
        assertTrue(limiter.tryAcquire(1_500_000))
    }

    @Test
    fun `latency is unknown until the host has related the two clocks`() {
        val clock = HostClockEstimate()

        assertFalse(clock.isKnown)
        assertNull(clock.latencyMicros(clientMicros = 5_000_000, hostCaptureMicros = 1_000_000))
        assertNull(clock.roundTripMicros)
    }

    @Test
    fun `latency is this device's time moved onto the host's clock, less the capture time`() {
        val clock = HostClockEstimate()
        // This device's clock reads 50 s more than the host's.
        clock.update(clientMinusHostMicros = 50_000_000, roundTripMicros = 1_800)

        // Captured at host time 10.000 s, shown here at device time 60.025 s, which is host time 10.025 s.
        assertEquals(25_000L, clock.latencyMicros(clientMicros = 60_025_000, hostCaptureMicros = 10_000_000))
        assertEquals(1_800L, clock.roundTripMicros)

        // The offset can just as well be negative.
        clock.update(clientMinusHostMicros = -3_000_000, roundTripMicros = 900)
        assertEquals(40_000L, clock.latencyMicros(clientMicros = 7_040_000, hostCaptureMicros = 10_000_000))

        clock.clear()
        assertNull(clock.latencyMicros(clientMicros = 7_040_000, hostCaptureMicros = 10_000_000))
    }

    @Test
    fun `a latency summary gives the count, the extremes, the mean and the 95th percentile in milliseconds`() {
        val tracker = LatencyTracker()
        (1..100).shuffled(java.util.Random(7)).forEach { tracker.add(it * 1_000L) }

        val summary = tracker.drain()!!

        assertEquals(LatencySummary(count = 100, minMs = 1.0, meanMs = 50.5, p95Ms = 95.0, maxMs = 100.0), summary)
    }

    @Test
    fun `draining starts the next interval empty`() {
        val tracker = LatencyTracker()
        assertNull(tracker.drain())

        tracker.add(20_000)
        assertEquals(1, tracker.drain()!!.count)
        assertNull(tracker.drain())

        tracker.add(30_000)
        assertEquals(30.0, tracker.drain()!!.meanMs, 0.0)
    }

    @Test
    fun `samples beyond the capacity are left out and the count says how many were kept`() {
        val tracker = LatencyTracker(capacity = 4)
        (1..10).forEach { tracker.add(it * 1_000L) }

        val summary = tracker.drain()!!

        assertEquals(4, summary.count)
        assertEquals(4.0, summary.maxMs, 0.0)
    }

    @Test
    fun `percentiles are nearest-rank`() {
        assertEquals(0, LatencyTracker.percentileIndex(1, 95.0))
        assertEquals(1, LatencyTracker.percentileIndex(2, 95.0))
        assertEquals(18, LatencyTracker.percentileIndex(20, 95.0))
        assertEquals(56, LatencyTracker.percentileIndex(60, 95.0))
        assertEquals(59, LatencyTracker.percentileIndex(60, 100.0))
    }

    @Test
    fun `the record of a connection states the counts and leaves unknown things as unknown`() {
        val summary = LiveSummary.format(
            LiveStatus(
                phase = LivePhase.Streaming,
                detail = "The host ended the session: the requested duration elapsed.",
                hostName = "DESKTOP-TEST",
                hostVersion = "0.1.0",
                config = StreamConfig("h264", "high", 1280, 720, 30, 6_000_000, Protocol.MAX_VIDEO_PAYLOAD, "Some Encoder MFT", "hardware"),
                pictureSize = "1280 x 720, High profile, level 3.1",
                decoderName = "c2.vendor.avc.decoder",
                decoderKind = "hardware",
                lowLatency = "requested; the decoder does not report the feature",
                stats = ClientStats(framesReceived = 900, bytesReceived = 22_500_000, framesSubmitted = 900, framesDecoded = 900, framesRendered = 900, framesDisplayed = 897),
                renderLatency = LatencySummary(30, 18.0, 22.44, 29.96, 41.0),
                roundTripMicros = 1_840,
                keyframeRequests = 1,
            ),
        )

        assertTrue(summary.contains("ended: The host ended the session: the requested duration elapsed."))
        assertTrue(summary.contains("stream announced: 1280 x 720 at 30 fps, 6000000 bit/s, encoder Some Encoder MFT (hardware)"))
        assertTrue(summary.contains("decoder: c2.vendor.avc.decoder [hardware]"))
        assertTrue(summary.contains("frames received: 900 (22500000 bytes)"))
        assertTrue(summary.contains("frames decoded and released to the surface: 900"))
        assertTrue(summary.contains("frames the system reported as displayed: 897"))
        assertTrue(summary.contains("last second, capture to release to the surface: mean 22.4 ms, p95 30.0, max 41.0 (n = 30)"))
        assertTrue(summary.contains("last second, capture to receive: n/a"))
        assertTrue(summary.contains("clock relation round trip: 1.8 ms"))
        assertTrue(summary.contains("problems: none"))
        assertFalse(summary.contains("null"))
    }

    @Test
    fun `input that ends is blamed on the host only when this side did not close it`() {
        assertEquals("The host closed the connection without saying goodbye.", describeEndOfInput(stoppedHere = false))
        assertEquals("Stopped: the live screen was closed.", describeEndOfInput(stoppedHere = true))
    }

    @Test
    fun `a read that fails after the timeout's worth of silence is reported as silence`() {
        assertEquals(
            "The connection to the host was lost: nothing arrived from it for 5 s.",
            describeLostConnection(quietMillis = 5_029, timeoutMillis = 5_000, cause = "Try again"),
        )
        assertEquals(
            "The connection to the host was lost: Connection reset by peer.",
            describeLostConnection(quietMillis = 12, timeoutMillis = 5_000, cause = "Connection reset by peer"),
        )
        assertEquals(
            "The connection to the host was lost: the read failed.",
            describeLostConnection(quietMillis = 0, timeoutMillis = 5_000, cause = null),
        )
    }

    private fun startedGate() = VideoGate().apply { onFrame(0, keyframe, hasSurface = true) }
}
