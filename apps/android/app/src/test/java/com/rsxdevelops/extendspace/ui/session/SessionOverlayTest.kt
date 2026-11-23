package com.rsxdevelops.extendspace.ui.session

import com.rsxdevelops.extendspace.R
import com.rsxdevelops.extendspace.connection.ConnectionError
import com.rsxdevelops.extendspace.connection.ConnectionErrorCode
import com.rsxdevelops.extendspace.connection.ConnectionState
import com.rsxdevelops.extendspace.connection.TransportKind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The session screen's content for every state (D66): what covers the picture and what the user can do. */
class SessionOverlayTest {
    private val usb = TransportKind.USB

    @Test
    fun `while streaming nothing covers the picture`() {
        assertNull(sessionOverlay(ConnectionState.Streaming(usb)))
    }

    @Test
    fun `waiting starting and reconnecting show progress and can be cancelled`() {
        for (state in listOf(ConnectionState.Connecting(usb), ConnectionState.Negotiating(usb), ConnectionState.Reconnecting(usb))) {
            val overlay = sessionOverlay(state)!!
            assertTrue("$state", overlay.showsProgress)
            assertEquals("$state", listOf(SessionAction.CANCEL), overlay.actions)
        }
        assertEquals(R.string.session_waiting_title, sessionOverlay(ConnectionState.Connecting(usb))!!.title)
        assertEquals(R.string.session_reconnecting_title, sessionOverlay(ConnectionState.Reconnecting(usb))!!.title)
    }

    @Test
    fun `while the session is ending nothing can be pressed`() {
        val overlay = sessionOverlay(ConnectionState.Disconnecting)!!
        assertTrue(overlay.showsProgress)
        assertTrue(overlay.actions.isEmpty())
    }

    @Test
    fun `an ended session offers to wait again or close`() {
        val overlay = sessionOverlay(ConnectionState.Disconnected)!!
        assertEquals(R.string.session_ended_title, overlay.title)
        assertEquals(listOf(SessionAction.WAIT_AGAIN, SessionAction.CLOSE), overlay.actions)
        assertFalse(overlay.showsProgress)
    }

    @Test
    fun `a failure says why and offers to try again or close, for every error code`() {
        for (code in ConnectionErrorCode.entries) {
            val overlay = sessionOverlay(ConnectionState.Failed(ConnectionError(code)))!!
            assertEquals(R.string.state_failed, overlay.title)
            assertEquals(code.message(), overlay.detail)
            assertEquals(listOf(SessionAction.TRY_AGAIN, SessionAction.CLOSE), overlay.actions)
        }
        assertEquals(R.string.error_decoder_unavailable, ConnectionErrorCode.DECODER_UNAVAILABLE.message())
    }

    @Test
    fun `leaving the screen ends the session exactly in the states that hold or wait for a link`() {
        assertTrue(ConnectionState.Connecting(usb).isActive)
        assertTrue(ConnectionState.Negotiating(usb).isActive)
        assertTrue(ConnectionState.Streaming(usb).isActive)
        assertTrue(ConnectionState.Reconnecting(usb).isActive)
        assertFalse(ConnectionState.Disconnecting.isActive)
        assertFalse(ConnectionState.Disconnected.isActive)
        assertFalse(ConnectionState.Failed(ConnectionError(ConnectionErrorCode.INTERNAL)).isActive)
    }
}
