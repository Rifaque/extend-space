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

    // A failed attempt goes back to where attempts are made from: Connecting for a session that has
    // never streamed, Reconnecting for one that has. Whether and when to try again is the session
    // controller's decision; nothing here retries.
    is ConnectionState.Connecting -> when {
        event == ConnectionEvent.TransportConnected ->
            ConnectionState.Negotiating(state.transport, resuming = false, lastAttemptFault = state.lastAttemptFault)
        event is ConnectionEvent.AttemptFailed ->
            if (event.fault.isRetryable) ConnectionState.Connecting(state.transport, event.fault) else null
        else -> whileActive(event)
    }

    is ConnectionState.Negotiating -> when {
        event == ConnectionEvent.NegotiationCompleted -> ConnectionState.Streaming(state.transport)
        event is ConnectionEvent.AttemptFailed -> when {
            !event.fault.isRetryable -> null
            state.resuming -> ConnectionState.Reconnecting(state.transport, event.fault)
            else -> ConnectionState.Connecting(state.transport, event.fault)
        }
        else -> whileActive(event)
    }

    // The loss is recorded like a failed attempt, so that the state can say why the session is
    // reconnecting before any attempt has failed. A terminal fault is refused, as for AttemptFailed.
    is ConnectionState.Streaming -> when {
        event is ConnectionEvent.ConnectionLost ->
            if (event.fault.isRetryable) ConnectionState.Reconnecting(state.transport, event.fault) else null
        else -> whileActive(event)
    }

    is ConnectionState.Reconnecting -> when {
        event == ConnectionEvent.TransportConnected ->
            ConnectionState.Negotiating(state.transport, resuming = true, lastAttemptFault = state.lastAttemptFault)
        event is ConnectionEvent.AttemptFailed ->
            if (event.fault.isRetryable) ConnectionState.Reconnecting(state.transport, event.fault) else null
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
