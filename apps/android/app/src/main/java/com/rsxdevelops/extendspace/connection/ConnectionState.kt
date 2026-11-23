package com.rsxdevelops.extendspace.connection

enum class TransportKind { USB, WIRELESS }

enum class ConnectionErrorCode {
    TRANSPORT_UNAVAILABLE,
    AUTHENTICATION_FAILED,
    INCOMPATIBLE_PROTOCOL,
    NEGOTIATION_FAILED,
    CONNECTION_LOST,

    /** No decoder on this device could be created for the stream the host offered. */
    DECODER_UNAVAILABLE,
    INTERNAL,
}

/**
 * Why a session failed, as the user is told.
 *
 * @param cause the fault behind it, for diagnostics. The code is the summary of it.
 */
data class ConnectionError(val code: ConnectionErrorCode, val detail: String? = null, val cause: SessionFault? = null)

/**
 * Lifecycle of the single session this device can hold with a Windows host.
 * Mirrors the session states in protocol/specification/protocol.md.
 */
sealed interface ConnectionState {
    data object Disconnected : ConnectionState
    /**
     * @param lastAttemptFault why the session is not streaming while it is still trying: the fault of
     * the most recent failed attempt, or of the loss that began reconnection. Cleared when
     * negotiation completes.
     */
    data class Connecting(val transport: TransportKind, val lastAttemptFault: SessionFault? = null) : ConnectionState
    /**
     * @param resuming true when this session has streamed before, so that a failed negotiation
     * returns to [Reconnecting] and not to [Connecting].
     */
    data class Negotiating(
        val transport: TransportKind,
        val resuming: Boolean = false,
        val lastAttemptFault: SessionFault? = null,
    ) : ConnectionState
    data class Streaming(val transport: TransportKind) : ConnectionState
    data class Reconnecting(val transport: TransportKind, val lastAttemptFault: SessionFault? = null) : ConnectionState
    data object Disconnecting : ConnectionState
    data class Failed(val error: ConnectionError) : ConnectionState
}

sealed interface ConnectionEvent {
    data class ConnectRequested(val transport: TransportKind) : ConnectionEvent
    data object TransportConnected : ConnectionEvent
    data object NegotiationCompleted : ConnectionEvent
    /**
     * A streaming link was lost without a goodbye. [fault] says why and must be retryable: a loss
     * nothing can recover from is reported as [Failed].
     */
    data class ConnectionLost(val fault: SessionFault) : ConnectionEvent

    /**
     * One attempt to connect or to negotiate failed for a reason that may not last, and the session
     * goes on. Not a wire message: it is the session controller telling the state machine what
     * happened. A fault that is not retryable is not an attempt failing; report it as [Failed].
     */
    data class AttemptFailed(val fault: SessionFault) : ConnectionEvent
    data object DisconnectRequested : ConnectionEvent
    data object TeardownCompleted : ConnectionEvent
    data class Failed(val error: ConnectionError) : ConnectionEvent
    data object ErrorAcknowledged : ConnectionEvent
}
