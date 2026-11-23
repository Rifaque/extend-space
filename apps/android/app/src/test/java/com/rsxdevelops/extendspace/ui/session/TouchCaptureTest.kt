package com.rsxdevelops.extendspace.ui.session

import com.rsxdevelops.extendspace.connection.TouchContactSample
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TouchCaptureTest {
    private val sent = mutableListOf<Pair<Long, List<TouchContactSample>>>()
    private var accept = true
    private val capture = TouchCapture { sequence, contacts -> if (accept) sent += sequence to contacts; accept }

    private fun frames(): List<String> = sent.map { (_, contacts) -> contacts.joinToString(" ") { "${it.id}:${PHASES[it.phase]}@${it.x},${it.y}" } }

    private fun p(id: Int, x: Float, y: Float) = PointerSample(id, x, y)

    @Test
    fun `positions are divided by the surface's size, which is the picture`() {
        capture.onEvent(TouchAction.DOWN, 0, listOf(p(0, 0f, 0f)), 1000, 500)
        capture.onEvent(TouchAction.MOVE, 0, listOf(p(0, 500f, 250f)), 1000, 500)
        capture.onEvent(TouchAction.UP, 0, listOf(p(0, 1000f, 500f)), 1000, 500)

        assertEquals(listOf("0:down@0,0", "0:move@32768,32768", "0:up@65535,65535"), frames())
        assertEquals(listOf(0L, 1L, 2L), sent.map { it.first })
    }

    @Test
    fun `a second finger lists both, and lifting one keeps the other`() {
        capture.onEvent(TouchAction.DOWN, 0, listOf(p(0, 0f, 0f)), 100, 100)
        capture.onEvent(TouchAction.POINTER_DOWN, 1, listOf(p(0, 0f, 0f), p(1, 100f, 100f)), 100, 100)
        capture.onEvent(TouchAction.POINTER_UP, 0, listOf(p(0, 10f, 0f), p(1, 100f, 100f)), 100, 100)
        capture.onEvent(TouchAction.MOVE, 1, listOf(p(1, 50f, 50f)), 100, 100)

        assertEquals(
            listOf("0:down@0,0", "0:move@0,0 1:down@65535,65535", "0:up@6554,0 1:move@65535,65535", "1:move@32768,32768"),
            frames(),
        )
    }

    @Test
    fun `a still finger's repeated moves are not sent again`() {
        capture.onEvent(TouchAction.DOWN, 0, listOf(p(0, 10f, 10f)), 100, 100)
        repeat(3) { capture.onEvent(TouchAction.MOVE, 0, listOf(p(0, 20f, 20f)), 100, 100) }
        assertEquals(2, sent.size)
    }

    @Test
    fun `cancel lifts every contact once, and later moves of them are not sent`() {
        capture.onEvent(TouchAction.DOWN, 0, listOf(p(0, 0f, 0f)), 100, 100)
        capture.onEvent(TouchAction.POINTER_DOWN, 1, listOf(p(0, 0f, 0f), p(1, 50f, 50f)), 100, 100)

        capture.cancelAll()
        capture.cancelAll()
        capture.onEvent(TouchAction.MOVE, 0, listOf(p(0, 10f, 10f), p(1, 60f, 60f)), 100, 100)

        assertEquals("0:cancel@0,0 1:cancel@32768,32768", frames().last())
        assertEquals(3, sent.size)
        assertEquals(0, capture.activeCount)
    }

    @Test
    fun `the system cancelling the gesture is the same as cancel all`() {
        capture.onEvent(TouchAction.DOWN, 0, listOf(p(0, 0f, 0f)), 100, 100)
        capture.onEvent(TouchAction.CANCEL, 0, listOf(p(0, 0f, 0f)), 100, 100)
        assertEquals("0:cancel@0,0", frames().last())
    }

    @Test
    fun `more than ten fingers, or pointer ids above 31, are ignored for their whole life`() {
        val pointers = (0 until 11).map { p(it, 1f, 1f) }
        capture.onEvent(TouchAction.DOWN, 0, pointers.take(1), 100, 100)
        for (n in 1 until 11) capture.onEvent(TouchAction.POINTER_DOWN, n, pointers.take(n + 1), 100, 100)
        capture.onEvent(TouchAction.POINTER_DOWN, 40, pointers.take(10) + p(40, 5f, 5f), 100, 100)

        assertEquals(10, capture.activeCount)
        assertTrue(sent.all { it.second.size <= 10 })
        assertTrue(sent.none { (_, contacts) -> contacts.any { it.id >= 10 } })
    }

    @Test
    fun `a new stream numbers its frames from zero and forgets held contacts`() {
        capture.onEvent(TouchAction.DOWN, 0, listOf(p(0, 0f, 0f)), 100, 100)
        capture.reset()
        capture.onEvent(TouchAction.DOWN, 3, listOf(p(3, 0f, 0f)), 100, 100)

        assertEquals(0L, sent.last().first)
        assertEquals("3:down@0,0", frames().last())
    }

    @Test
    fun `nothing is sent for a surface that has no size yet`() {
        capture.onEvent(TouchAction.DOWN, 0, listOf(p(0, 0f, 0f)), 0, 0)
        assertTrue(sent.isEmpty())
    }

    @Test
    fun `a frame the link refused does not use up a sequence number`() {
        accept = false
        capture.onEvent(TouchAction.DOWN, 0, listOf(p(0, 0f, 0f)), 100, 100)
        accept = true
        capture.onEvent(TouchAction.MOVE, 0, listOf(p(0, 1f, 0f)), 100, 100)
        assertEquals(0L, sent.single().first)
    }

    private companion object {
        val PHASES = listOf("down", "move", "up", "cancel")
    }
}
