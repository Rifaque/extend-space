package com.rsxdevelops.extendspace.video

import com.rsxdevelops.extendspace.connection.AttemptOutcome
import com.rsxdevelops.extendspace.connection.DecoderChoice
import com.rsxdevelops.extendspace.connection.FaultCategory
import com.rsxdevelops.extendspace.connection.FaultTreatment
import com.rsxdevelops.extendspace.connection.StreamDescription
import com.rsxdevelops.extendspace.protocol.ProtocolException
import com.rsxdevelops.extendspace.video.H264Samples.KEYFRAME_FLAGS
import com.rsxdevelops.extendspace.video.H264Samples.delta
import com.rsxdevelops.extendspace.video.H264Samples.keyframe
import com.rsxdevelops.extendspace.video.H264Samples.payload
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/**
 * The production receiver's policy with a scripted decoder platform: decoder choice, creation at
 * the first keyframe once there is both a stream and an output, the output lent and withdrawn at
 * any point, stale decoders, decoder failures, malformed video, and nothing left after a stop. No
 * `MediaCodec` is involved; whether a real decoder shows a picture is a device test.
 */
class MediaCodecVideoReceiverTest {
    private val platform = FakePlatform()
    private var now = 0L
    // Decoders are made at once on the calling thread here; the tests of the owner thread itself are in MediaCodecVideoReceiverOwnerTest.
    private val receiver = MediaCodecVideoReceiver(platform, nowMicros = { now }, owner = InlineOwner)
    private val stream = StreamDescription("h264", 1920, 1080, 60, "TEST-PC", "1.2.3")
    private val choice = DecoderChoice("c2.fake.avc.decoder", true)
    private val requests = mutableListOf<String>()
    private var sequence = 0L

    private fun start() = receiver.start(stream, choice) { synchronized(requests) { requests.add(it) } }

    /** One frame; its timestamp is its sequence number, so that the order frames reach a decoder can be checked. */
    private fun send(accessUnit: ByteArray, flags: Int = 0, sequenceNumber: Long = sequence++) {
        val bytes = payload(sequenceNumber, accessUnit, timestampMicros = sequenceNumber)
        receiver.sink.accept(flags, bytes, bytes.size)
    }

    private fun sendKeyframe(sps: ByteArray = H264Samples.SPS_1080P) = send(keyframe(sps), KEYFRAME_FLAGS)

    @Test
    fun `the decoders offered and the one chosen come from the platform`() = runBlocking {
        assertEquals(platform.decoders(), receiver.decoders())
        assertEquals(AttemptOutcome.Succeeded(choice), receiver.prepare(stream))
    }

    @Test
    fun `a stream no decoder can take, or not h264, is a terminal decoder fault`() = runBlocking {
        val hevc = receiver.prepare(stream.copy(codec = "h265")) as AttemptOutcome.Failed
        assertEquals(FaultCategory.DECODER to FaultTreatment.TERMINAL, hevc.fault.category to hevc.fault.treatment)
        assertEquals("codec_unsupported", hevc.fault.code)

        platform.choice = null
        val none = receiver.prepare(stream) as AttemptOutcome.Failed
        assertEquals("decoder_unavailable", none.fault.code)
        assertEquals(FaultCategory.DECODER, none.fault.category)
    }

    @Test
    fun `preparing chooses a decoder without creating one, and nothing is decoded before a stream starts`() = runBlocking {
        receiver.setOutput(FakeOutput("a"))
        receiver.prepare(stream)
        sendKeyframe()
        assertTrue(platform.created.isEmpty())
        assertNull(receiver.stats())
    }

    @Test
    fun `the first keyframe creates the decoder on the lent output and is submitted to it`() {
        val output = FakeOutput("a")
        receiver.setOutput(output)
        start()
        sendKeyframe()
        send(delta)

        val decoder = platform.created.single()
        assertSame(output, decoder.output)
        assertEquals(1920 to 1080, decoder.parameters.width to decoder.parameters.height)
        assertEquals(listOf(true, false), decoder.submitted.map { it.second })
        assertEquals(2L, receiver.stats()!!.framesSubmitted)
    }

    @Test
    fun `frames before the first keyframe are discarded and ask for one once, and again only after a second without one`() {
        // D61: until D61 it asked at most once per 500 ms, and the link could drop what it asked.
        receiver.setOutput(FakeOutput("a"))
        start()
        repeat(10) { send(delta) }
        now += 999_999
        send(delta)
        assertEquals(listOf("startup"), requests)
        now += 1
        send(delta)

        assertTrue(platform.created.isEmpty())
        assertEquals(listOf("startup", "startup"), requests)
        assertEquals(12L, receiver.stats()!!.framesDiscarded)
    }

    @Test
    fun `with no output at stream start frames are discarded without asking, and no decoder exists`() {
        start()
        sendKeyframe()
        send(delta)

        assertTrue(platform.created.isEmpty())
        assertTrue(requests.isEmpty())
        assertEquals(2L, receiver.stats()!!.framesDiscarded)
    }

    @Test
    fun `an output lent during a stream is used from the next keyframe`() {
        start()
        sendKeyframe()
        receiver.setOutput(FakeOutput("late"))
        send(delta)
        assertTrue(platform.created.isEmpty())

        sendKeyframe()
        assertEquals("late", (platform.created.single().output as FakeOutput).name)
    }

    @Test
    fun `withdrawing the output releases the decoder before returning and decoding resumes only at a keyframe after it returns`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        sendKeyframe()
        val first = platform.created.single()

        receiver.setOutput(null)
        assertEquals(1, first.released)
        assertFalse(receiver.hasDecoder)
        send(delta)

        receiver.setOutput(FakeOutput("b"))
        send(delta)
        assertEquals(1, platform.created.size)
        sendKeyframe()
        assertEquals("b", (platform.created.last().output as FakeOutput).name)
        assertEquals(1, platform.live.size)
    }

    @Test
    fun `replacing the output releases the decoder drawing to the old one`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        sendKeyframe()
        receiver.setOutput(FakeOutput("b"))
        assertEquals(1, platform.created.single().released)
        sendKeyframe()
        assertEquals(listOf("a", "b"), platform.created.map { (it.output as FakeOutput).name })
        assertEquals(1, platform.live.size)
    }

    @Test
    fun `a keyframe with the same parameter sets keeps the decoder and different ones replace it`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        sendKeyframe()
        sendKeyframe()
        assertEquals(1, platform.created.size)

        // The old decoder is gone from the output before the new one is made on it.
        var releasedBefore = -1
        platform.duringCreate = { releasedBefore = platform.created[0].released }
        sendKeyframe(H264Samples.SPS_720P)
        assertEquals(2, platform.created.size)
        assertEquals(1, releasedBefore)
        assertEquals(1, platform.created[0].released)
        assertEquals(1280 to 720, platform.created[1].parameters.width to platform.created[1].parameters.height)
    }

    @Test
    fun `a sequence gap breaks the chain until a keyframe and asks for one with its reason`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        sendKeyframe()
        send(delta, sequenceNumber = 5)
        assertEquals(listOf("sequence_gap"), requests)
        assertEquals(4L, receiver.stats()!!.framesMissing)
        assertEquals(1, platform.created.single().submitted.size)

        send(keyframe(), KEYFRAME_FLAGS, sequenceNumber = 6)
        assertEquals(2, platform.created.single().submitted.size)
    }

    @Test
    fun `a decoder that reports an error is released at the next frame and replaced at the next keyframe`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        sendKeyframe()
        platform.created.single().listener.onDecoderError("The decoder failed: test")
        send(delta)

        assertEquals(1, platform.created.single().released)
        assertEquals(listOf("decode_error"), requests)
        assertEquals(1L, receiver.stats()!!.decoderErrors)
        now += 1_000_000
        sendKeyframe()
        assertEquals(2, platform.created.size)
    }

    @Test
    fun `a decoder too far behind drops to the next keyframe and asks for one`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        sendKeyframe()
        platform.created.single().accepting = false
        send(delta)
        assertEquals(listOf("decoder_backlog"), requests)
    }

    @Test
    fun `a decoder that cannot be created for the stream is a terminal decoder fault`() = runBlocking {
        receiver.setOutput(FakeOutput("a"))
        start()
        platform.createFailure = IllegalStateException("no codec instance left")
        sendKeyframe()

        val fault = receiver.awaitFault()!!
        assertEquals(FaultCategory.DECODER to "decoder_start_failed", fault.category to fault.code)
        assertEquals(FaultTreatment.TERMINAL, fault.treatment)
    }

    @Test
    fun `malformed video is a protocol violation that the link turns into a fatal error`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        assertThrows(ProtocolException::class.java) { receiver.sink.accept(0, ByteArray(12), 12) }
        assertThrows(ProtocolException::class.java) { send(H264Samples.keyframeWithoutParameterSets, KEYFRAME_FLAGS) }
        assertThrows(ProtocolException::class.java) { send(keyframe(byteArrayOf(0x67, 0x64)), KEYFRAME_FLAGS) }
        assertTrue(platform.created.isEmpty())
    }

    @Test
    fun `stopping releases the decoder, ends the fault wait with nothing, keeps the output, and ignores later video`() = runBlocking {
        val output = FakeOutput("a")
        receiver.setOutput(output)
        start()
        sendKeyframe()
        receiver.stop()
        receiver.stop()

        assertEquals(1, platform.created.single().released)
        assertNull(receiver.awaitFault())
        sendKeyframe()
        assertEquals(1, platform.created.size)
        assertNull(receiver.stats())

        // The output is still lent: the next stream decodes onto it without being lent again.
        start()
        sendKeyframe()
        assertSame(output, platform.created.last().output)
        assertEquals(1, platform.live.size)
    }

    @Test
    fun `a reader thread overtaken by a withdrawal never installs a decoder on the withdrawn output`() {
        // Many rounds of a keyframe on the reader thread racing a withdrawal on another: whatever
        // the interleaving, no live decoder may be left on an output that was taken back.
        repeat(200) { round ->
            val output = FakeOutput("round $round")
            receiver.setOutput(output)
            start()
            val go = CountDownLatch(1)
            val reader = thread {
                go.await()
                sendKeyframe()
            }
            go.countDown()
            receiver.setOutput(null)
            reader.join(TimeUnit.SECONDS.toMillis(5))
            assertTrue("round $round", platform.live.none { it.output === output })
            runBlocking { receiver.stop() }
        }
    }

    @Test
    fun `an output withdrawn after the gate let a keyframe through but before the decoder lock gets no decoder`() {
        // The window D55 closed, entered on purpose rather than by chance: the gate saw an output,
        // then the withdrawal completes before the reader takes the lock. Nothing may be made on it.
        var withdrawInWindow = false
        lateinit var hooked: MediaCodecVideoReceiver
        hooked = MediaCodecVideoReceiver(platform, nowMicros = { now }, owner = InlineOwner, beforeDecoderLock = {
            if (withdrawInWindow) {
                withdrawInWindow = false
                hooked.setOutput(null)
            }
        })
        fun feedKeyframe(sequenceNumber: Long) = payload(sequenceNumber, keyframe()).let { hooked.sink.accept(KEYFRAME_FLAGS, it, it.size) }

        hooked.setOutput(FakeOutput("withdrawn"))
        hooked.start(stream, choice) { synchronized(requests) { requests.add(it) } }
        withdrawInWindow = true
        feedKeyframe(0)

        assertTrue(platform.created.isEmpty())
        assertFalse(hooked.hasDecoder)
        assertEquals(1L, hooked.stats()!!.framesDiscarded)
        assertEquals(listOf("surface_recreated"), requests)

        // Lent again: the next keyframe is decoded on the new output, and only there.
        val again = FakeOutput("lent again")
        hooked.setOutput(again)
        feedKeyframe(1)
        assertSame(again, platform.created.single().output)
        assertEquals(1, platform.live.size)
    }

    @Test
    fun `a failure reported by a decoder no longer in use does not take its successor out of use`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        sendKeyframe()
        val first = platform.created.single()
        receiver.setOutput(FakeOutput("b"))
        sendKeyframe()
        val second = platform.created.last()

        first.listener.onDecoderError("The decoder failed: late, from a decoder already released")
        send(delta)

        assertEquals(0, second.released)
        assertTrue(receiver.hasDecoder)
        assertEquals(listOf(true, false), second.submitted.map { it.second })
        assertEquals(1L, receiver.stats()!!.decoderErrors)
    }

    // ---- D61: recovery from a backlog --------------------------------------------------------------

    /**
     * A decoder with its real input bookkeeping, running and caught up, its first keyframe in the
     * codec. From here the test decides when the codec takes pictures ([FakeDecoder.codecTakes]).
     */
    private fun runningDecoder(): FakeDecoder {
        platform.withBacklog = true
        receiver.setOutput(FakeOutput("a"))
        start()
        sendKeyframe()
        val decoder = platform.created.single()
        decoder.producedOutput()
        decoder.codecTakes(1)
        assertEquals(listOf(0L), decoder.codecInput.map { it.first })
        return decoder
    }

    /** Frames the codec does not keep up with, until one is refused: the backlog. */
    private fun backlog(decoder: FakeDecoder) {
        repeat(InputBacklog.STEADY_PICTURES) { send(delta) }
        assertEquals(InputBacklog.STEADY_PICTURES, decoder.waiting)
        send(delta)
    }

    @Test
    fun `a backlog asks once, and the keyframe that answers supersedes the pictures still waiting so that only it and what follows reach the codec`() {
        val decoder = runningDecoder()
        backlog(decoder)
        assertEquals(listOf("decoder_backlog"), requests)
        repeat(5) { send(delta) }
        assertEquals(listOf("decoder_backlog"), requests)

        val keyframeSequence = sequence
        sendKeyframe()
        send(delta)
        send(delta)
        decoder.codecTakes(10)

        // Nothing from before the keyframe follows it into the codec: not the pictures that waited, not the refused one, not the ones after.
        assertEquals(listOf(0L, keyframeSequence, keyframeSequence + 1, keyframeSequence + 2), decoder.codecInput.map { it.first })
        assertEquals(listOf(true, true, false, false), decoder.codecInput.map { it.second })
        val stats = receiver.stats()!!
        // Submitted: the two keyframes and the two deltas after the second. Discarded: 1 refused, 5 behind the gate, 4 superseded.
        assertEquals(4L to 10L, stats.framesSubmitted to stats.framesDiscarded)
        assertEquals(stats.framesReceived, stats.framesSubmitted + stats.framesDiscarded)
        assertEquals(1, platform.created.size)
    }

    @Test
    fun `a keyframe ends the recovery, so the next backlog asks at once`() {
        val decoder = runningDecoder()
        backlog(decoder)
        sendKeyframe()
        decoder.codecTakes(1)
        backlog(decoder)
        assertEquals(listOf("decoder_backlog", "decoder_backlog"), requests)
    }

    @Test
    fun `while no keyframe comes, the recovery asks again once a second and no more`() {
        val decoder = runningDecoder()
        backlog(decoder)
        repeat(30) {
            now += 100_000
            send(delta)
        }
        assertEquals(4, requests.size)
        assertTrue(requests.all { it == "decoder_backlog" })
    }

    @Test
    fun `steady playback never enters recovery`() {
        val decoder = runningDecoder()
        repeat(200) {
            now += 16_667
            send(delta)
            decoder.codecTakes(1)
        }
        assertTrue(requests.isEmpty())
        assertEquals(0L, receiver.stats()!!.framesDiscarded)
        assertEquals(201, decoder.codecInput.size)
    }

    @Test
    fun `a keyframe with other parameter sets during a backlog gets a new decoder, and the old one's waiting pictures go with it`() {
        val first = runningDecoder()
        backlog(first)
        val keyframeSequence = sequence
        sendKeyframe(H264Samples.SPS_720P)

        assertEquals(1, first.released)
        assertEquals(listOf(0L), first.codecInput.map { it.first })
        val second = platform.created.last()
        assertEquals(1280 to 720, second.parameters.width to second.parameters.height)
        second.codecTakes(1)
        assertEquals(listOf(keyframeSequence), second.codecInput.map { it.first })
    }

    @Test
    fun `withdrawing the output during a recovery drops what waited with its decoder, and the next decoder starts at a keyframe`() {
        val first = runningDecoder()
        backlog(first)
        receiver.setOutput(null)
        assertEquals(1, first.released)
        repeat(3) { send(delta) }

        receiver.setOutput(FakeOutput("b"))
        send(delta)
        // The controller asks when an output is lent; the recovery already asked and does not again within the second.
        assertEquals(listOf("decoder_backlog"), requests)
        val keyframeSequence = sequence
        sendKeyframe()
        val second = platform.created.last()
        second.codecTakes(5)
        assertEquals(listOf(keyframeSequence), second.codecInput.map { it.first })
    }

    @Test
    fun `a stop during a recovery ends it, and a new stream asks at once`() = runBlocking {
        val decoder = runningDecoder()
        backlog(decoder)
        receiver.stop()
        repeat(3) { send(delta) }
        assertEquals(listOf("decoder_backlog"), requests)
        assertEquals(1, decoder.released)

        start()
        send(delta)
        assertEquals(listOf("decoder_backlog", "startup"), requests)
    }
}
