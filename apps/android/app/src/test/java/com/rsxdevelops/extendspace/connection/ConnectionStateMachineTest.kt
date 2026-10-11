package com.rsxdevelops.extendspace.connection

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ConnectionStateMachineTest {
    private val lost = ConnectionError(ConnectionErrorCode.CONNECTION_LOST)

    private fun machineIn(vararg events: ConnectionEvent) = ConnectionStateMachine().apply {
        events.forEach { assertTrue("setup event $it was rejected", dispatch(it)) }
    }

    private val connect = ConnectionEvent.ConnectRequested(TransportKind.USB)
    private val toStreaming = arrayOf(
        connect,
        ConnectionEvent.TransportConnected,
        ConnectionEvent.NegotiationCompleted,
    )

    @Test
    fun `starts disconnected`() {
        assertEquals(ConnectionState.Disconnected, ConnectionStateMachine().state.value)
    }

    @Test
    fun `connect flows through negotiating to streaming and keeps the transport`() {
        val machine = ConnectionStateMachine()

        assertTrue(machine.dispatch(ConnectionEvent.ConnectRequested(TransportKind.WIRELESS)))
        assertEquals(ConnectionState.Connecting(TransportKind.WIRELESS), machine.state.value)

        assertTrue(machine.dispatch(ConnectionEvent.TransportConnected))
        assertEquals(ConnectionState.Negotiating(TransportKind.WIRELESS), machine.state.value)

        assertTrue(machine.dispatch(ConnectionEvent.NegotiationCompleted))
        assertEquals(ConnectionState.Streaming(TransportKind.WIRELESS), machine.state.value)
    }

    @Test
    fun `losing the connection while streaming starts a reconnect that renegotiates`() {
        val machine = machineIn(*toStreaming)

        assertTrue(machine.dispatch(ConnectionEvent.ConnectionLost))
        assertEquals(ConnectionState.Reconnecting(TransportKind.USB), machine.state.value)

        assertTrue(machine.dispatch(ConnectionEvent.TransportConnected))
        assertEquals(ConnectionState.Negotiating(TransportKind.USB), machine.state.value)
    }

    @Test
    fun `every in-progress state can be disconnected and tears down to disconnected`() {
        val inProgress = listOf(
            machineIn(connect),
            machineIn(connect, ConnectionEvent.TransportConnected),
            machineIn(*toStreaming),
            machineIn(*toStreaming, ConnectionEvent.ConnectionLost),
        )

        inProgress.forEach { machine ->
            assertTrue(machine.dispatch(ConnectionEvent.DisconnectRequested))
            assertEquals(ConnectionState.Disconnecting, machine.state.value)
            assertTrue(machine.dispatch(ConnectionEvent.TeardownCompleted))
            assertEquals(ConnectionState.Disconnected, machine.state.value)
        }
    }

    @Test
    fun `a failure is held until acknowledged`() {
        val machine = machineIn(connect)

        assertTrue(machine.dispatch(ConnectionEvent.Failed(lost)))
        assertEquals(ConnectionState.Failed(lost), machine.state.value)

        assertFalse(machine.dispatch(connect))
        assertEquals(ConnectionState.Failed(lost), machine.state.value)

        assertTrue(machine.dispatch(ConnectionEvent.ErrorAcknowledged))
        assertEquals(ConnectionState.Disconnected, machine.state.value)
    }

    @Test
    fun `events that do not apply are rejected without changing state`() {
        val machine = ConnectionStateMachine()

        assertFalse(machine.dispatch(ConnectionEvent.TransportConnected))
        assertFalse(machine.dispatch(ConnectionEvent.NegotiationCompleted))
        assertFalse(machine.dispatch(ConnectionEvent.DisconnectRequested))
        assertFalse(machine.dispatch(ConnectionEvent.Failed(lost)))
        assertEquals(ConnectionState.Disconnected, machine.state.value)
    }

    @Test
    fun `a second connect request is rejected while a session is in progress`() {
        val machine = machineIn(*toStreaming)

        assertFalse(machine.dispatch(ConnectionEvent.ConnectRequested(TransportKind.WIRELESS)))
        assertEquals(ConnectionState.Streaming(TransportKind.USB), machine.state.value)
    }

    @Test
    fun `nothing but teardown completion leaves disconnecting`() {
        val state = ConnectionState.Disconnecting

        assertNull(nextState(state, connect))
        assertNull(nextState(state, ConnectionEvent.Failed(lost)))
        assertNull(nextState(state, ConnectionEvent.DisconnectRequested))
        assertEquals(
            ConnectionState.Disconnected,
            nextState(state, ConnectionEvent.TeardownCompleted),
        )
    }

    @Test
    fun `idle controller reports disconnected with no hosts`() {
        val controller = IdleConnectionController()

        assertEquals(ConnectionState.Disconnected, controller.state.value)
        assertTrue(controller.hosts.value.isEmpty())
    }
}
