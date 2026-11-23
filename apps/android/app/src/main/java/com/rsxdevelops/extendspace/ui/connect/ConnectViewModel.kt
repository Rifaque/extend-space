package com.rsxdevelops.extendspace.ui.connect

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider.AndroidViewModelFactory.Companion.APPLICATION_KEY
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.rsxdevelops.extendspace.ExtendSpaceApplication
import com.rsxdevelops.extendspace.connection.ConnectionController
import com.rsxdevelops.extendspace.connection.ConnectionState
import com.rsxdevelops.extendspace.connection.HostDevice
import com.rsxdevelops.extendspace.connection.TransportKind
import com.rsxdevelops.extendspace.connection.WaitResult
import com.rsxdevelops.extendspace.wireless.PairingState
import com.rsxdevelops.extendspace.wireless.TrustedHost
import com.rsxdevelops.extendspace.wireless.WirelessClient
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn

data class ConnectUiState(
    val connection: ConnectionState = ConnectionState.Disconnected,
    val hosts: List<HostDevice> = emptyList(),
    /** Whether Wait for PC also accepts paired PCs over Wi-Fi (D74). */
    val wifiEnabled: Boolean = false,
    val pairing: PairingState = PairingState.Closed,
    val pairedPcs: List<TrustedHost> = emptyList(),
    /** The route the PC is connected by, while it is. */
    val connectedOver: TransportKind? = null,
    /** This tablet's short ID as the PC lists it. */
    val tabletId: String? = null,
    /** False in a build or preview without the wireless parts. */
    val wirelessAvailable: Boolean = false,
    /** This tablet's address on the local network, to type on the PC if it is not found (ES-126). */
    val tabletAddress: String? = null,
)

class ConnectViewModel(
    private val controller: ConnectionController,
    private val wireless: WirelessClient? = null,
) : ViewModel() {
    private val noWifi = MutableStateFlow(false)
    private val noPairing = MutableStateFlow<PairingState>(PairingState.Closed)
    private val noPaired = MutableStateFlow<List<TrustedHost>>(emptyList())
    private val tabletId = wireless?.shortId()

    private val session = combine(controller.state, controller.hosts, controller.connectedOver) { state, hosts, over -> Triple(state, hosts, over) }
    private val wifi = combine(wireless?.wifiEnabled ?: noWifi, wireless?.pairing ?: noPairing, wireless?.pairedPcs ?: noPaired) { on, pairing, paired -> Triple(on, pairing, paired) }

    val uiState: StateFlow<ConnectUiState> =
        combine(session, wifi) { (state, hosts, over), (on, pairing, paired) ->
            ConnectUiState(state, hosts, on, pairing, paired, over, tabletId, wireless != null, if (on) wireless?.localAddresses()?.firstOrNull() else null)
        }.stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5_000),
            initialValue = ConnectUiState(
                controller.state.value,
                controller.hosts.value,
                wireless?.wifiEnabled?.value ?: false,
                wireless?.pairing?.value ?: PairingState.Closed,
                wireless?.pairedPcs?.value.orEmpty(),
                controller.connectedOver.value,
                tabletId,
                wireless != null,
            ),
        )

    /**
     * Starts waiting for a PC (D66): over USB, and over Wi-Fi too when allowed (D74), clearing a
     * failure the user has seen first. A session already in progress is not an error: the screen
     * then just opens it.
     */
    fun waitForPc(): WaitResult {
        if (controller.state.value is ConnectionState.Failed) controller.acknowledgeError()
        return controller.waitForHost(if (wireless?.wifiEnabled?.value == true) TransportKind.WIRELESS else TransportKind.USB)
    }

    fun setWifiEnabled(enabled: Boolean) = wireless?.setWifiEnabled(enabled)

    fun openPairing() = wireless?.openPairing()

    fun closePairing() = wireless?.closePairing()

    fun confirmPairing(accepted: Boolean) = wireless?.confirmPairing(accepted)

    fun acknowledgePairing() = wireless?.acknowledgePairing()

    fun forget(host: TrustedHost) = wireless?.forget(host.deviceId)

    companion object {
        val Factory = viewModelFactory {
            initializer {
                val application = this[APPLICATION_KEY] as ExtendSpaceApplication
                ConnectViewModel(application.connectionController, application.wireless)
            }
        }
    }
}
