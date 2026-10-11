package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Returns the state that follows [state] when [event] occurs, or null if the event is not valid
 * in that state.
 */
fun nextState(state: ConnectionState, event: ConnectionEvent): ConnectionState? = when (state) {
    ConnectionState.Disconnected -> when (event) {
        is ConnectionEvent.ConnectRequested -> ConnectionState.Connecting(event.transport)
        else -> null
    }

    is ConnectionState.Connecting -> when (event) {
        ConnectionEvent.TransportConnected -> ConnectionState.Negotiating(state.transport)
        else -> whileActive(event)
    }

    is ConnectionState.Negotiating -> when (event) {
        ConnectionEvent.NegotiationCompleted -> ConnectionState.Streaming(state.transport)
        else -> whileActive(event)
    }

    is ConnectionState.Streaming -> when (event) {
        ConnectionEvent.ConnectionLost -> ConnectionState.Reconnecting(state.transport)
        else -> whileActive(event)
    }

    is ConnectionState.Reconnecting -> when (event) {
        ConnectionEvent.TransportConnected -> ConnectionState.Negotiating(state.transport)
        else -> whileActive(event)
    }

    ConnectionState.Disconnecting -> when (event) {
        ConnectionEvent.TeardownCompleted -> ConnectionState.Disconnected
        else -> null
    }

    is ConnectionState.Failed -> when (event) {
        ConnectionEvent.ErrorAcknowledged -> ConnectionState.Disconnected
        else -> null
    }
}

/** Events every in-progress state accepts: the user can always disconnect, and anything can fail. */
private fun whileActive(event: ConnectionEvent): ConnectionState? = when (event) {
    ConnectionEvent.DisconnectRequested -> ConnectionState.Disconnecting
    is ConnectionEvent.Failed -> ConnectionState.Failed(event.error)
    else -> null
}

/** Thread-safe holder for the current [ConnectionState]. */
class ConnectionStateMachine(initial: ConnectionState = ConnectionState.Disconnected) {
    private val _state = MutableStateFlow(initial)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    /** Applies [event]. Returns false, leaving the state unchanged, if it is not valid right now. */
    fun dispatch(event: ConnectionEvent): Boolean {
        while (true) {
            val current = _state.value
            val next = nextState(current, event) ?: return false
            if (_state.compareAndSet(current, next)) return true
        }
    }
}
