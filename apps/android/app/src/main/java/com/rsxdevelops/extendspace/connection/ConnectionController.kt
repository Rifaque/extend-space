package com.rsxdevelops.extendspace.connection

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** A Windows host this device has paired with or discovered. */
data class HostDevice(val id: String, val displayName: String, val transport: TransportKind)

/**
 * The only surface the UI uses to observe connections. Transports, pairing and the video pipeline
 * live behind it; connect and disconnect operations are added with the first real transport.
 */
interface ConnectionController {
    val state: StateFlow<ConnectionState>
    val hosts: StateFlow<List<HostDevice>>
}

/**
 * Stand-in until a transport exists. Nothing dispatches events to it, so it stays
 * [ConnectionState.Disconnected] and never reports a host.
 */
class IdleConnectionController : ConnectionController {
    private val stateMachine = ConnectionStateMachine()

    override val state: StateFlow<ConnectionState> = stateMachine.state
    override val hosts: StateFlow<List<HostDevice>> =
        MutableStateFlow<List<HostDevice>>(emptyList()).asStateFlow()
}
