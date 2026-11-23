package com.rsxdevelops.extendspace.video

import com.rsxdevelops.extendspace.connection.DecoderChoice
import com.rsxdevelops.extendspace.connection.SessionFault
import com.rsxdevelops.extendspace.connection.StreamDescription
import com.rsxdevelops.extendspace.link.PATIENCE_MS
import com.rsxdevelops.extendspace.video.H264Samples.KEYFRAME_FLAGS
import com.rsxdevelops.extendspace.video.H264Samples.delta
import com.rsxdevelops.extendspace.video.H264Samples.keyframe
import com.rsxdevelops.extendspace.video.H264Samples.payload
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread

/**
 * The receiver's decoder owner (D59): decoders are created on it, so that a creation that takes its
 * time holds up neither the reader thread nor the UI; what a creation makes is used only if it is
 * still wanted when it returns; frames that arrive meanwhile wait in order, within a bound; and a
 * stop waits, suspended, for everything the owner has to release. Creations are held open with
 * latches, not timed. A scripted platform stands in for `MediaCodec`.
 */
class MediaCodecVideoReceiverOwnerTest {
    private val platform = FakePlatform()
    private val owner = MediaCodecVideoReceiver.decoderOwner(idleMillis = 200) as ThreadPoolExecutor
    private val receiver = MediaCodecVideoReceiver(platform, owner = owner)
    private val stream = StreamDescription("h264", 1920, 1080, 60, "TEST-PC", "1.2.3")
    private val choice = DecoderChoice("c2.fake.avc.decoder", true)
    private val requests = mutableListOf<String>()
    private var sequence = 0L

    @After
    fun tearDown() {
        runBlocking { receiver.stop() }
        owner.shutdownNow()
    }

    private fun start(into: MediaCodecVideoReceiver = receiver) = into.start(stream, choice) { synchronized(requests) { requests.add(it) } }

    private fun send(accessUnit: ByteArray, flags: Int = 0, into: MediaCodecVideoReceiver = receiver) {
        val number = sequence++
        val bytes = payload(number, accessUnit, timestampMicros = number)
        into.sink.accept(flags, bytes, bytes.size)
    }

    private fun sendKeyframe(sps: ByteArray = H264Samples.SPS_1080P, into: MediaCodecVideoReceiver = receiver) = send(keyframe(sps), KEYFRAME_FLAGS, into)

    /** Holds the next creation open until the returned latch is counted down; [creating] opens once it is inside. */
    private fun holdCreation(creating: CountDownLatch, afterwards: () -> Unit = {}): CountDownLatch {
        val proceed = CountDownLatch(1)
        val first = AtomicInteger()
        platform.duringCreate = {
            if (first.getAndIncrement() == 0) {
                creating.countDown()
                proceed.await(HOLD_MS, TimeUnit.MILLISECONDS)
                afterwards()
            }
        }
        return proceed
    }

    /** The stream's fault if it has one, null while it is running. */
    private fun faultSoFar(of: MediaCodecVideoReceiver = receiver): SessionFault? = runBlocking { withTimeoutOrNull(200) { of.awaitFault() } }

    private companion object {
        /** The longest a test holds a creation open; bounded so that a receiver that blocks on it fails instead of hanging. */
        const val HOLD_MS = 2 * PATIENCE_MS
    }

    private fun awaitUntil(condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + PATIENCE_MS
        while (!condition()) {
            check(System.currentTimeMillis() < deadline) { "not reached in time" }
            Thread.sleep(2)
        }
    }

    @Test
    fun `a decoder being created holds up neither the reader nor the statistics, and the frames that arrive meanwhile follow its keyframe in order`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        val creating = CountDownLatch(1)
        val proceed = holdCreation(creating)

        // This thread is the reader: each call returns while the creation is held open.
        sendKeyframe()
        assertTrue(creating.await(PATIENCE_MS, TimeUnit.MILLISECONDS))
        repeat(5) { send(delta) }
        assertTrue(receiver.isCreatingDecoder)
        assertFalse(receiver.hasDecoder)

        // The writer's statistics read the receiver's lock, which the creation does not hold.
        val stats = receiver.stats()!!
        assertEquals(6L to 0L, stats.framesReceived to stats.framesSubmitted)
        assertEquals(0L to 6, stats.framesDiscarded to stats.queueDepth)

        proceed.countDown()
        awaitUntil { receiver.hasDecoder }
        send(delta)
        val decoder = platform.created.single()
        assertEquals((0L..6L).toList(), decoder.submitted.map { it.first })
        assertEquals(listOf(true) + List(6) { false }, decoder.submitted.map { it.second })
        assertEquals(7L to 0L, receiver.stats()!!.let { it.framesSubmitted to it.framesDiscarded })
        assertTrue(requests.isEmpty())
    }

    @Test
    fun `a withdrawal during a creation returns at once, and what the creation makes is released without ever being given a frame`() {
        val output = FakeOutput("withdrawn")
        receiver.setOutput(output)
        start()
        val creating = CountDownLatch(1)
        val proceed = holdCreation(creating)
        sendKeyframe()
        assertTrue(creating.await(PATIENCE_MS, TimeUnit.MILLISECONDS))
        send(delta)

        // The UI thread is not held by the creation (D59; until now it waited for it).
        receiver.setOutput(null)
        assertEquals(1L, proceed.count)
        assertFalse(receiver.isCreatingDecoder)

        proceed.countDown()
        awaitUntil { platform.created.size == 1 && platform.created.single().released == 1 }
        val made = platform.created.single()
        assertSame(output, made.output)
        assertTrue(made.submitted.isEmpty())
        assertFalse(receiver.hasDecoder)
        assertEquals(2L, receiver.stats()!!.framesDiscarded)
        assertNull(faultSoFar())
    }

    @Test
    fun `an output replaced during a creation gets the next decoder, made only after the stale one is released`() {
        receiver.setOutput(FakeOutput("before rotation"))
        start()
        val creating = CountDownLatch(1)
        val proceed = holdCreation(creating)
        sendKeyframe()
        assertTrue(creating.await(PATIENCE_MS, TimeUnit.MILLISECONDS))

        val after = FakeOutput("after rotation")
        receiver.setOutput(after)
        // The keyframe the controller asks for when an output is lent, while the first creation is still held.
        var staleReleasedFirst: Boolean? = null
        sendKeyframe()
        val second = platform.duringCreate
        platform.duringCreate = {
            second()
            if (platform.created.size == 1) staleReleasedFirst = platform.created[0].released == 1
        }
        proceed.countDown()

        awaitUntil { receiver.hasDecoder }
        assertEquals(listOf("before rotation", "after rotation"), platform.created.map { (it.output as FakeOutput).name })
        assertEquals(true, staleReleasedFirst)
        assertTrue(platform.created[0].submitted.isEmpty())
        assertEquals(listOf(true), platform.created[1].submitted.map { it.second })
        assertEquals(listOf(platform.created[1]), platform.live)
        assertSame(after, platform.live.single().output)
    }

    @Test
    fun `a stop during a creation waits for it, suspended, and what it makes is released and never used`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        val creating = CountDownLatch(1)
        val proceed = holdCreation(creating)
        sendKeyframe()
        assertTrue(creating.await(PATIENCE_MS, TimeUnit.MILLISECONDS))

        val stopped = CountDownLatch(1)
        val stopping = thread {
            runBlocking { receiver.stop() }
            stopped.countDown()
        }
        // D58: the stop returns only once everything is released, so it waits for the creation.
        assertFalse(stopped.await(100, TimeUnit.MILLISECONDS))

        proceed.countDown()
        assertTrue(stopped.await(PATIENCE_MS, TimeUnit.MILLISECONDS))
        stopping.join()
        assertEquals(1, platform.created.single().released)
        assertTrue(platform.created.single().submitted.isEmpty())
        assertFalse(receiver.hasDecoder)

        // Nothing comes back to life: video after the stop makes nothing.
        sendKeyframe()
        Thread.sleep(50)
        assertEquals(1, platform.created.size)
        assertFalse(receiver.isCreatingDecoder)
    }

    @Test
    fun `a stale creation that fails does not fault a newer stream, whose own decoder is then made`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        val creating = CountDownLatch(1)
        val proceed = holdCreation(creating) { throw IllegalStateException("the surface was abandoned during configure") }
        sendKeyframe()
        assertTrue(creating.await(PATIENCE_MS, TimeUnit.MILLISECONDS))

        // A new stream, before the old creation has returned; its own first keyframe waits behind it.
        start()
        sendKeyframe()
        proceed.countDown()

        awaitUntil { receiver.hasDecoder }
        assertNull(faultSoFar())
        assertEquals(1, platform.created.size)
        assertEquals(listOf(true), platform.created.single().submitted.map { it.second })
    }

    @Test
    fun `a stale creation that fails after the output was replaced does not end the stream`() {
        receiver.setOutput(FakeOutput("destroyed"))
        start()
        val creating = CountDownLatch(1)
        val proceed = holdCreation(creating) { throw IllegalStateException("the surface was abandoned during configure") }
        sendKeyframe()
        assertTrue(creating.await(PATIENCE_MS, TimeUnit.MILLISECONDS))

        receiver.setOutput(FakeOutput("new"))
        proceed.countDown()
        awaitUntil { !receiver.isCreatingDecoder }
        sendKeyframe()
        awaitUntil { receiver.hasDecoder }

        assertNull(faultSoFar())
        assertEquals("new", (platform.created.single().output as FakeOutput).name)
    }

    @Test
    fun `the creation still wanted failing is a terminal decoder fault, and its waiting frames are discarded`() {
        receiver.setOutput(FakeOutput("a"))
        start()
        val creating = CountDownLatch(1)
        val proceed = holdCreation(creating) { throw IllegalStateException("no codec instance left") }
        sendKeyframe()
        assertTrue(creating.await(PATIENCE_MS, TimeUnit.MILLISECONDS))
        send(delta)
        proceed.countDown()

        val fault = runBlocking { receiver.awaitFault() }!!
        assertEquals("decoder_start_failed", fault.code)
        assertEquals(2L to 0L, receiver.stats()!!.let { it.framesDiscarded to it.framesSubmitted })
    }

    @Test
    fun `at most thirty frames wait for a decoder, and one more drops them all and asks for a keyframe`() {
        val manual = ManualOwner()
        val stepped = MediaCodecVideoReceiver(platform, owner = manual)
        stepped.setOutput(FakeOutput("a"))
        start(stepped)

        sendKeyframe(into = stepped)
        repeat(MediaCodecVideoReceiver.MAX_WAITING_FRAMES - 1) { send(delta, into = stepped) }
        assertEquals(MediaCodecVideoReceiver.MAX_WAITING_FRAMES, stepped.stats()!!.queueDepth)
        assertEquals(1, manual.queued)
        send(delta, into = stepped)
        assertEquals(listOf("decoder_backlog"), requests)
        assertEquals(MediaCodecVideoReceiver.MAX_WAITING_FRAMES + 1L, stepped.stats()!!.framesDiscarded)
        assertEquals(0, stepped.stats()!!.queueDepth)

        // Deltas cannot follow what was dropped; the decoder is still made, and starts at the next keyframe.
        send(delta, into = stepped)
        manual.runAll()
        assertTrue(stepped.hasDecoder)
        assertTrue(platform.created.single().submitted.isEmpty())
        sendKeyframe(into = stepped)
        assertEquals(listOf(true), platform.created.single().submitted.map { it.second })
        assertEquals(1L, stepped.stats()!!.framesSubmitted)
    }

    @Test
    fun `a keyframe that would overflow the frames waiting for a decoder replaces them, without asking for another`() {
        // D61: until D61 it was dropped with them, and the stream waited for yet another keyframe.
        val manual = ManualOwner()
        val stepped = MediaCodecVideoReceiver(platform, owner = manual)
        stepped.setOutput(FakeOutput("a"))
        start(stepped)
        sendKeyframe(into = stepped)
        repeat(MediaCodecVideoReceiver.MAX_WAITING_FRAMES - 1) { send(delta, into = stepped) }

        val keyframe = sequence
        sendKeyframe(into = stepped)
        assertTrue(requests.isEmpty())
        assertEquals(1, stepped.stats()!!.queueDepth)
        assertEquals(MediaCodecVideoReceiver.MAX_WAITING_FRAMES.toLong(), stepped.stats()!!.framesDiscarded)

        send(delta, into = stepped)
        manual.runAll()
        assertEquals(listOf(keyframe, keyframe + 1), platform.created.single().submitted.map { it.first })
    }

    @Test
    fun `the frames waiting for a decoder are also bounded in bytes`() {
        val manual = ManualOwner()
        val stepped = MediaCodecVideoReceiver(platform, owner = manual)
        stepped.setOutput(FakeOutput("a"))
        start(stepped)
        val half = ByteArray((MediaCodecVideoReceiver.MAX_WAITING_BYTES / 2).toInt())

        send(keyframe() + half, KEYFRAME_FLAGS, into = stepped)
        assertEquals(1, stepped.stats()!!.queueDepth)
        send(delta + half, into = stepped)
        assertEquals(0, stepped.stats()!!.queueDepth)
        assertEquals(2L, stepped.stats()!!.framesDiscarded)
        assertEquals(listOf("decoder_backlog"), requests)
    }

    @Test
    fun `keyframes with new parameter sets while a creation waits replace it, and only the newest is made`() {
        val manual = ManualOwner()
        val stepped = MediaCodecVideoReceiver(platform, owner = manual)
        stepped.setOutput(FakeOutput("a"))
        start(stepped)

        sendKeyframe(into = stepped)
        sendKeyframe(H264Samples.SPS_720P, into = stepped)
        send(delta, into = stepped)
        // One task at most is ever queued on the owner, however many creations are asked for.
        assertEquals(1, manual.queued)
        manual.runAll()

        val made = platform.created.single()
        assertEquals(1280 to 720, made.parameters.width to made.parameters.height)
        assertEquals(listOf(true, false), made.submitted.map { it.second })
        assertEquals(1L, stepped.stats()!!.framesDiscarded)
    }

    @Test
    fun `repeated sessions with withdrawals racing the reader leave no decoder and no owner thread, and never overlap two operations on a decoder`() {
        platform.duringSubmit = { Thread.sleep(0, 200_000) }
        repeat(40) { round ->
            val output = FakeOutput("round $round")
            receiver.setOutput(output)
            start()
            val go = CountDownLatch(1)
            val reader = thread {
                go.await(HOLD_MS, TimeUnit.MILLISECONDS)
                sendKeyframe()
                repeat(10) { send(delta) }
                sendKeyframe()
            }
            go.countDown()
            if (round % 2 == 0) receiver.setOutput(null)
            reader.join(PATIENCE_MS)
            runBlocking { receiver.stop() }

            assertTrue("round $round: ${platform.live}", platform.live.isEmpty())
            assertFalse(receiver.hasDecoder || receiver.isCreatingDecoder)
            receiver.setOutput(null)
        }
        assertTrue(platform.overlaps.toString(), platform.overlaps.isEmpty())
        assertEquals(1, platform.mostCreatingAtOnce)
        // The owner's thread ends once it has had nothing to do for its idle time.
        awaitUntil { owner.poolSize == 0 }
    }
}
