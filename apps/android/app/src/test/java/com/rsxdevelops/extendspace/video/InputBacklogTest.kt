package com.rsxdevelops.extendspace.video

import com.rsxdevelops.extendspace.video.InputBacklog.Offer
import com.rsxdevelops.extendspace.video.InputBacklog.Phase
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The bookkeeping between a decoder's input buffers and the pictures given to it (D60), with time
 * given by hand: what waits, in which order, how much may wait in each phase, and when the startup
 * allowance ends. `SurfaceDecoder` runs exactly this under its lock; whether a real codec decodes
 * and draws the pictures is a device test.
 */
class InputBacklogTest {
    private val backlog = InputBacklog<Int>().apply { started(0) }
    private var next = 0

    private fun ms(millis: Long) = millis * 1_000_000

    /** Offers the next picture (numbered in order) of [bytes] bytes at [atMillis]; a [keyFrame] is an IDR picture. */
    private fun offer(atMillis: Long, bytes: Int = 25_000, keyFrame: Boolean = false): Offer {
        val number = next++
        return backlog.offer(ms(atMillis), bytes, keyFrame) { number }
    }

    private fun offerWaiting(count: Int, atMillis: Long) = repeat(count) { assertEquals(Offer.Waiting, offer(atMillis)) }

    /** The codec takes [count] waiting pictures; returns their numbers. */
    private fun take(count: Int, atMillis: Long): List<Int> = List(count) { backlog.inputAvailable(ms(atMillis), 100 + it)!! }

    @Test
    fun `before its first output a decoder keeps the startup burst, in order, and refuses beyond thirty`() {
        offerWaiting(30, atMillis = 10)
        assertTrue(offer(20) is Offer.Refused)
        assertEquals(Phase.STARTING, backlog.phase)
        assertEquals(30, backlog.depth)
        assertEquals((0 until 30).toList(), take(30, atMillis = 30))
    }

    @Test
    fun `free input buffers are used before anything waits, and a picture waits only when none is free`() {
        assertNull(backlog.inputAvailable(ms(1), 7))
        assertNull(backlog.inputAvailable(ms(1), 8))
        assertEquals(Offer.Input(7), offer(2))
        assertEquals(Offer.Input(8), offer(2))
        assertEquals(Offer.Waiting, offer(2))
        assertEquals(1, backlog.depth)
    }

    @Test
    fun `the backlog measured on the Pad 7 is worked off without a refusal`() {
        // Section 20.11: 24 pictures were waiting when the decoder started (the frames that waited
        // for its creation); at its first output, 47 ms in, 15 still waited; at 65 ms one more
        // arrived with 12 waiting and was refused, and at 131 ms another with 4 waiting.
        offerWaiting(24, atMillis = 0)
        take(9, atMillis = 40)
        backlog.outputProduced(ms(47))
        assertEquals(Phase.CATCHING_UP to 15, backlog.phase to backlog.depth)

        take(3, atMillis = 60)
        assertEquals(Offer.Waiting, offer(65))
        take(9, atMillis = 120)
        assertEquals(4, backlog.depth)
        assertEquals(Offer.Waiting, offer(131))
        assertEquals(Phase.CATCHING_UP, backlog.phase)

        take(3, atMillis = 140)
        assertEquals(Phase.STEADY to 2, backlog.phase to backlog.depth)
        assertEquals(24, backlog.mostWaiting)
        // Every picture comes out in the order it went in.
        assertEquals(listOf(24, 25), take(2, atMillis = 150))
    }

    @Test
    fun `the startup allowance ends once caught up, for good, and the steady limit applies from then on`() {
        offerWaiting(10, atMillis = 0)
        backlog.outputProduced(ms(50))
        take(6, atMillis = 60)
        assertEquals(Phase.CATCHING_UP to 4, backlog.phase to backlog.depth)
        take(1, atMillis = 61)
        assertEquals(Phase.STEADY to 3, backlog.phase to backlog.depth)

        // Behind again: the steady limit, not the startup one.
        assertEquals(Offer.Waiting, offer(70))
        val refused = offer(70)
        assertTrue(refused is Offer.Refused)
        assertTrue((refused as Offer.Refused).reason, refused.reason.contains("limit 4 pictures"))
        assertEquals(Phase.STEADY to 4, backlog.phase to backlog.depth)
    }

    @Test
    fun `a decoder with no output within a second loses the startup allowance`() {
        offerWaiting(10, atMillis = 0)
        assertEquals(Offer.Waiting, offer(999))
        val refused = offer(1_000)
        assertTrue(refused is Offer.Refused)
        assertEquals(Phase.STEADY, backlog.phase)
        assertTrue(backlog.takeNotes().single().contains("before its first output"))
    }

    @Test
    fun `a decoder that never catches up loses the startup allowance after a second, and the backlog is then refused`() {
        offerWaiting(20, atMillis = 0)
        backlog.outputProduced(ms(100))
        // It keeps taking pictures, but no faster than they arrive.
        for (at in 110L..990L step 50) {
            take(1, atMillis = at)
            assertEquals(Offer.Waiting, offer(at))
        }
        assertEquals(Phase.CATCHING_UP, backlog.phase)
        assertTrue(offer(1_000) is Offer.Refused)
        assertEquals(Phase.STEADY, backlog.phase)
        assertTrue(backlog.takeNotes().last().contains("without catching up"))
    }

    @Test
    fun `what waits is bounded in bytes in every phase`() {
        val big = (InputBacklog.MAX_BYTES / 2).toInt()
        assertEquals(Offer.Waiting, offer(0, bytes = big))
        assertEquals(Offer.Waiting, offer(0, bytes = big))
        val refused = offer(0, bytes = 1)
        assertTrue(refused is Offer.Refused)
        assertEquals(Phase.STARTING, backlog.phase)
        assertEquals(InputBacklog.MAX_BYTES, backlog.waitingBytes)

        take(1, atMillis = 1)
        assertEquals(InputBacklog.MAX_BYTES / 2, backlog.waitingBytes)
        assertEquals(Offer.Waiting, offer(1, bytes = 1))
    }

    @Test
    fun `repeated refusals in steady state never let more than the steady limit wait`() {
        backlog.outputProduced(ms(10))
        assertEquals(Phase.STEADY, backlog.phase)
        var refusals = 0
        for (round in 0 until 50) {
            repeat(10) { if (offer(20L + round) is Offer.Refused) refusals++ }
            assertTrue(backlog.depth <= InputBacklog.STEADY_PICTURES)
            take(2, atMillis = 20L + round)
        }
        assertTrue(refusals > 0)
        assertTrue(backlog.mostWaiting <= InputBacklog.STEADY_PICTURES)
    }

    @Test
    fun `phase changes are noted once each, and nothing else is`() {
        offerWaiting(8, atMillis = 0)
        assertTrue(backlog.takeNotes().isEmpty())
        backlog.outputProduced(ms(40))
        backlog.outputProduced(ms(41))
        take(5, atMillis = 50)
        val notes = backlog.takeNotes()
        assertEquals(2, notes.size)
        assertTrue(notes[0], notes[0].startsWith("first output 40 ms after it started; 8 pictures waiting"))
        assertTrue(notes[1], notes[1].startsWith("caught up 50 ms after it started: 3 pictures waiting, most so far 8"))
        repeat(20) { offer(60); take(1, atMillis = 60) }
        assertTrue(backlog.takeNotes().isEmpty())
    }

    @Test
    fun `clearing forgets every picture and input buffer`() {
        assertNull(backlog.inputAvailable(ms(0), 3))
        offer(0)
        offerWaiting(5, atMillis = 0)
        backlog.clear()
        assertEquals(0 to 0L, backlog.depth to backlog.waitingBytes)
        assertEquals(Offer.Waiting, offer(1))
    }

    // ---- D61: a keyframe at capacity -------------------------------------------------------------

    /** A steady decoder with the steady limit's worth of pictures waiting. */
    private fun steadyAndFull() {
        backlog.outputProduced(ms(10))
        assertEquals(Phase.STEADY, backlog.phase)
        offerWaiting(InputBacklog.STEADY_PICTURES, atMillis = 20)
        assertTrue(offer(21) is Offer.Refused)
        backlog.takeNotes()
    }

    @Test
    fun `a keyframe offered at capacity supersedes the pictures waiting and takes their place, ahead of what follows`() {
        steadyAndFull()
        val keyframe = next
        assertEquals(Offer.Waiting, offer(22, keyFrame = true))
        assertEquals(InputBacklog.STEADY_PICTURES, backlog.takeSuperseded())
        assertEquals(0, backlog.takeSuperseded())
        assertEquals(1, backlog.depth)
        assertTrue(backlog.takeNotes().single().startsWith("keyframe superseded 4 pictures"))

        offerWaiting(2, atMillis = 23)
        assertEquals(listOf(keyframe, keyframe + 1, keyframe + 2), take(3, atMillis = 30))
    }

    @Test
    fun `a keyframe that fits waits behind the pictures before it, which are kept`() {
        backlog.outputProduced(ms(10))
        offerWaiting(2, atMillis = 20)
        assertEquals(Offer.Waiting, offer(21, keyFrame = true))
        assertEquals(0, backlog.takeSuperseded())
        assertEquals(listOf(0, 1, 2), take(3, atMillis = 30))
    }

    @Test
    fun `at capacity a picture that is not a keyframe is still refused, and nothing waiting is dropped`() {
        steadyAndFull()
        assertTrue(offer(22) is Offer.Refused)
        assertEquals(0, backlog.takeSuperseded())
        assertEquals(InputBacklog.STEADY_PICTURES, backlog.depth)
    }

    @Test
    fun `during startup a keyframe at the startup limit supersedes as well, and the startup rules are otherwise unchanged`() {
        offerWaiting(InputBacklog.STARTUP_PICTURES, atMillis = 0)
        assertEquals(Offer.Waiting, offer(5, keyFrame = true))
        assertEquals(InputBacklog.STARTUP_PICTURES, backlog.takeSuperseded())
        assertEquals(Phase.STARTING to 1, backlog.phase to backlog.depth)
        backlog.outputProduced(ms(40))
        assertEquals(Phase.STEADY, backlog.phase)
    }

    @Test
    fun `a keyframe over the byte bound supersedes what waits only if it fits alone`() {
        val big = (InputBacklog.MAX_BYTES / 2).toInt()
        assertEquals(Offer.Waiting, offer(0, bytes = big))
        assertEquals(Offer.Waiting, offer(0, bytes = big))
        assertEquals(Offer.Waiting, offer(1, bytes = big, keyFrame = true))
        assertEquals(2, backlog.takeSuperseded())
        assertEquals(big.toLong(), backlog.waitingBytes)
        assertTrue(offer(2, bytes = (InputBacklog.MAX_BYTES + 1).toInt(), keyFrame = true) is Offer.Refused)
        assertEquals(0, backlog.takeSuperseded())
    }
}
