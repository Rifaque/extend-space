package com.rsxdevelops.extendspace.livestream

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/** The pieces of leaving the live screen that must not depend on the other device answering. */
class ShutdownTest {
    @Test
    fun `a goodbye that goes out at once is followed by the close`() {
        val order = CopyOnWriteArrayList<String>()

        val said = sayGoodbyeThenClose(budgetMillis = 2_000, goodbye = { order.add("goodbye") }, close = { order.add("close") })

        assertTrue(said)
        assertEquals(listOf("goodbye", "close"), order)
    }

    @Test
    fun `a goodbye blocked on a peer that is not reading does not hold up the close`() {
        val socketClosed = CountDownLatch(1)
        val writeReturned = CountDownLatch(1)
        val started = System.nanoTime()

        val said = sayGoodbyeThenClose(
            budgetMillis = 200,
            goodbye = {
                // A write to a frozen host: it returns only when the socket is closed under it.
                socketClosed.await(30, TimeUnit.SECONDS)
                writeReturned.countDown()
            },
            close = { socketClosed.countDown() },
        )
        val tookMillis = (System.nanoTime() - started) / 1_000_000

        assertFalse("the goodbye cannot have finished", said)
        assertTrue("closing waited $tookMillis ms for a write that could not complete", tookMillis < 2_000)
        assertTrue("closing the socket must free the blocked write", writeReturned.await(5, TimeUnit.SECONDS))
    }

    @Test
    fun `a goodbye that fails is still followed by the close`() {
        val closes = AtomicInteger()

        val said = sayGoodbyeThenClose(budgetMillis = 2_000, goodbye = { throw IllegalStateException("codec gone") }, close = { closes.incrementAndGet() })

        assertTrue("a goodbye that failed has still finished", said)
        assertEquals(1, closes.get())
    }

    @Test
    fun `the close happens exactly once however the goodbye ends`() {
        val closes = AtomicInteger()
        val release = CountDownLatch(1)

        sayGoodbyeThenClose(budgetMillis = 50, goodbye = { release.await(5, TimeUnit.SECONDS) }, close = { closes.incrementAndGet() })
        release.countDown()
        Thread.sleep(100)

        assertEquals(1, closes.get())
    }

    @Test
    fun `a name that is still held is tried again until it is free`() {
        val attempts = AtomicInteger()
        val pauses = mutableListOf<Long>()

        val result = retryOnIoFailure(attempts = 5, pauseMillis = 100, pause = { pauses.add(it) }) {
            if (attempts.incrementAndGet() < 3) throw IOException("Address already in use") else "bound"
        }

        assertEquals("bound", result)
        assertEquals(3, attempts.get())
        assertEquals(listOf(100L, 100L), pauses)
    }

    @Test
    fun `a name that never comes free fails with the last error after the allowed tries`() {
        val attempts = AtomicInteger()

        val failure = assertThrows(IOException::class.java) {
            retryOnIoFailure<String>(attempts = 4, pauseMillis = 1, pause = {}) {
                throw IOException("Address already in use, try ${attempts.incrementAndGet()}")
            }
        }

        assertEquals(4, attempts.get())
        assertEquals("Address already in use, try 4", failure.message)
    }

    @Test
    fun `retrying stops as soon as it is no longer wanted`() {
        val attempts = AtomicInteger()

        assertThrows(IOException::class.java) {
            retryOnIoFailure<String>(attempts = 50, pauseMillis = 1, keepTrying = { attempts.get() < 2 }, pause = {}) {
                attempts.incrementAndGet()
                throw IOException("Address already in use")
            }
        }

        assertEquals(2, attempts.get())
    }
}
