package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.CancellableContinuation
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.suspendCancellableCoroutine
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.coroutines.resume

class SessionFaultTest {
    @Test
    fun `a fault says whether trying again can help`() {
        val closed = SessionFault(FaultCategory.CONNECTION, "host_closed_connection", "Closed.", FaultTreatment.RETRYABLE)
        val mismatch = SessionFault(FaultCategory.PROTOCOL, "protocol_version_unsupported", "Another version.", FaultTreatment.TERMINAL)

        assertTrue(closed.isRetryable)
        assertFalse(mismatch.isRetryable)
        assertFalse(closed.userActionable)
    }

    @Test
    fun `an unexpected exception keeps its type and message and is terminal`() {
        val fault = SessionFault.unexpected(IllegalStateException("the codec was released"))

        assertEquals(FaultCategory.INTERNAL, fault.category)
        assertEquals(FaultTreatment.TERMINAL, fault.treatment)
        assertEquals("IllegalStateException: the codec was released", fault.message)
    }

    @Test
    fun `an outcome is exactly one of succeeded, failed and cancelled`() {
        val fault = SessionFault(FaultCategory.TIMEOUT, "handshake_timeout", "No hello.", FaultTreatment.RETRYABLE)
        val outcomes: List<AttemptOutcome<String>> = listOf(AttemptOutcome.Succeeded("ready"), AttemptOutcome.Failed(fault), AttemptOutcome.Cancelled)

        val described = outcomes.map { outcome ->
            when (outcome) {
                is AttemptOutcome.Succeeded -> "succeeded with ${outcome.value}"
                is AttemptOutcome.Failed -> "failed with ${outcome.fault.code}"
                // Cancellation carries no fault: it is not something that went wrong.
                AttemptOutcome.Cancelled -> "cancelled"
            }
        }

        assertEquals(listOf("succeeded with ready", "failed with handshake_timeout", "cancelled"), described)
    }
}

/**
 * The clock seam, proven usable: a clock a test moves by hand makes a wait finish exactly when the
 * test says, without the test sleeping.
 */
class SessionClockTest {
    @Test
    fun `a delay returns when the clock is moved past it and not before`() {
        val clock = ManualSessionClock()
        val scope = CoroutineScope(Dispatchers.Unconfined)
        var finished = false

        scope.launch {
            clock.delay(4_000)
            finished = true
        }
        clock.advance(3_999)
        assertFalse(finished)

        clock.advance(1)
        assertTrue(finished)
        assertEquals(4_000, clock.elapsedMillis())
    }

    @Test
    fun `delays return in the order they fall due`() {
        val clock = ManualSessionClock()
        val scope = CoroutineScope(Dispatchers.Unconfined)
        val order = mutableListOf<String>()

        scope.launch { clock.delay(2_000); order.add("late") }
        scope.launch { clock.delay(1_000); order.add("early") }
        clock.advance(5_000)

        assertEquals(listOf("early", "late"), order)
    }

    @Test
    fun `a cancelled delay never returns and is forgotten`() {
        val clock = ManualSessionClock()
        val scope = CoroutineScope(Dispatchers.Unconfined)
        var finished = false

        scope.launch {
            clock.delay(30_000)
            finished = true
        }
        scope.cancel()
        clock.advance(60_000)

        assertFalse(finished)
        assertEquals(0, clock.pendingDelays)
    }

    @Test
    fun `the real clock moves forward`() = runBlocking {
        val clock = SystemSessionClock()
        val before = clock.elapsedMillis()
        clock.delay(20)
        assertTrue(clock.elapsedMillis() > before)
    }
}

/** A clock that stands still until a test moves it. Not thread-safe; tests drive it from one thread. */
internal class ManualSessionClock : SessionClock {
    private class Waiting(val due: Long, val continuation: CancellableContinuation<Unit>)

    private val waiting = mutableListOf<Waiting>()
    private var now = 0L

    val pendingDelays: Int get() = waiting.size

    override fun elapsedMillis(): Long = now

    override suspend fun delay(millis: Long) {
        if (millis <= 0) return
        suspendCancellableCoroutine { continuation ->
            val entry = Waiting(now + millis, continuation)
            waiting.add(entry)
            continuation.invokeOnCancellation { waiting.remove(entry) }
        }
    }

    fun advance(millis: Long) {
        val target = now + millis
        while (true) {
            val next = waiting.filter { it.due <= target }.minByOrNull { it.due } ?: break
            waiting.remove(next)
            now = next.due
            next.continuation.resume(Unit)
        }
        now = target
    }
}
