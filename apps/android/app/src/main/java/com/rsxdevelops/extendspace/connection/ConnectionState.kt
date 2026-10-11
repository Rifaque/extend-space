package com.rsxdevelops.extendspace.connection

enum class TransportKind { USB, WIRELESS }

enum class ConnectionErrorCode {
    TRANSPORT_UNAVAILABLE,
    AUTHENTICATION_FAILED,
    INCOMPATIBLE_PROTOCOL,
    NEGOTIATION_FAILED,
    CONNECTION_LOST,
    INTERNAL,
}

data class ConnectionError(val code: ConnectionErrorCode, val detail: String? = null)

/**
 * Lifecycle of the single session this device can hold with a Windows host.
 * Mirrors the session states in protocol/specification/protocol.md.
 */
sealed interface ConnectionState {
    data object Disconnected : ConnectionState
    data class Connecting(val transport: TransportKind) : ConnectionState
    data class Negotiating(val transport: TransportKind) : ConnectionState
    data class Streaming(val transport: TransportKind) : ConnectionState
    data class Reconnecting(val transport: TransportKind) : ConnectionState
    data object Disconnecting : ConnectionState
    data class Failed(val error: ConnectionError) : ConnectionState
}

sealed interface ConnectionEvent {
    data class ConnectRequested(val transport: TransportKind) : ConnectionEvent
    data object TransportConnected : ConnectionEvent
    data object NegotiationCompleted : ConnectionEvent
    data object ConnectionLost : ConnectionEvent
    data object DisconnectRequested : ConnectionEvent
    data object TeardownCompleted : ConnectionEvent
    data class Failed(val error: ConnectionError) : ConnectionEvent
    data object ErrorAcknowledged : ConnectionEvent
}
